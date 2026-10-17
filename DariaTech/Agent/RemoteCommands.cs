using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed partial class DuplicatiAdapter
{
 public async Task<long> Dispatch(DeviceCommand command,AgentOptions options,CancellationToken ct)
 {
  var jobs=await ReadJobs(ct);if(jobs.All(x=>x.LocalId!=command.LocalJobId))throw new InvalidOperationException("Backup job no longer exists");
  // Two devices writing to one destination would corrupt it; a recovery copy only restores.
  if(command.Action==RemoteAction.RunBackup&&IsRestoreOnly(command.LocalJobId))throw new InvalidOperationException("Recovery copy cannot run backups");
  // Another device's original paths are not this device's; a recovery copy only restores into a new folder.
  if(command.Action==RemoteAction.Restore&&command.Restore?.OriginalLocation==true&&IsRestoreOnly(command.LocalJobId))throw new InvalidOperationException("Recovery copy restores into a new folder only");
  if(command.Action is RemoteAction.RunBackup or RemoteAction.VerifyBackup&&IsAdopting(command.LocalJobId))throw new InvalidOperationException("Taken-over backup is still rebuilding its database");
  var cloudBindings=state.Read<Dictionary<string,SaasJobBinding>>("saas-bindings.bin")??[];
  if(command.Action==RemoteAction.RunBackup&&cloudBindings.TryGetValue(command.LocalJobId,out var cloud))await RequireSaasProvider(cloud.Source,false,ct);
  if(command.Action==RemoteAction.RestoreProxmox)return await RestoreProxmox(command,ct);
  if(command.Action==RemoteAction.RestoreSaas)return await RestoreSaas(command,ct);
  HttpResponseMessage response;
  if(command.Action==RemoteAction.StopBackup)
  {
   var progress=await ReadProgress(ct);if(progress is null||progress.LocalJobId!=command.LocalJobId||progress.TaskId<1)throw new InvalidOperationException("No matching active task");
   using var stopped=await client.PostAsync($"/api/v1/task/{progress.TaskId}/stop",null,ct);stopped.EnsureSuccessStatusCode();return progress.TaskId;
  }
  if(command.Action==RemoteAction.Restore)
  {
   var restore=command.Restore!;var time=restore.Snapshot.ToUniversalTime().ToString("O");
   // Original location: the engine writes to the original paths and keeps existing files (overwrite=false
   // stores the restored version next to them with a timestamp suffix).
   if(restore.OriginalLocation)response=await client.PostAsJsonAsync($"/api/v1/backup/{command.LocalJobId}/restore",new{paths=restore.Paths,time,overwrite=false,permissions=false,skip_metadata=false},ct);
   else response=await client.PostAsJsonAsync($"/api/v1/backup/{command.LocalJobId}/restore",new{paths=restore.Paths,time,restore_path=RestoreDestination(options.RestoreRoot,restore.DestinationFolder),overwrite=false,permissions=false,skip_metadata=true},ct);
  }
  else response=await client.PostAsync($"/api/v1/backup/{command.LocalJobId}/{(command.Action==RemoteAction.RunBackup?"run":"verify")}",null,ct);
  using(response)
  {
   response.EnsureSuccessStatusCode();using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
   if(!long.TryParse(Get(result.RootElement,"ID").ToString(),out var id)||id<1)throw new HttpRequestException("Invalid engine task receipt");
   if(command.Action==RemoteAction.RunBackup&&(cloudBindings.ContainsKey(command.LocalJobId)||(state.Read<Dictionary<string,string>>("source-jobs.bin")??[]).ContainsKey(command.LocalJobId)))
   {
    var tasks=state.Read<Dictionary<long,CloudTaskBinding>>("cloud-tasks.bin")??[];
    foreach(var expired in tasks.Where(x=>x.Value.Dispatched<DateTimeOffset.UtcNow.AddDays(-1)).Select(x=>x.Key).ToArray())tasks.Remove(expired);
    if(tasks.Count>=1000)throw new InvalidOperationException("Cloud task journal exceeds retention bound");
    tasks[id]=new(command.LocalJobId,DateTimeOffset.UtcNow);state.Write("cloud-tasks.bin",tasks);
   }
   return id;
  }
 }
 public async Task<CommandReceipt> TaskReceipt(long id,CancellationToken ct)
 {
  using var response=await client.GetAsync($"/api/v1/task/{id}",ct);response.EnsureSuccessStatusCode();using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  var status=Get(result.RootElement,"Status").GetString();
  // Engine tasks can return normally with a failed result (notably partial restores).
  // Never forward raw exception/error text, and never turn this into a successful receipt.
  var errorMessage=Get(result.RootElement,"ErrorMessage").ToString();var exception=Get(result.RootElement,"Exception").ToString();
  if(status=="Completed"&&(!string.IsNullOrWhiteSpace(errorMessage)||!string.IsNullOrWhiteSpace(exception)))return new("Failed",id,EngineErrors.Classify(errorMessage,exception));
  if(status=="Completed"&&(state.Read<Dictionary<long,CloudTaskBinding>>("cloud-tasks.bin")??[]).TryGetValue(id,out var cloud))
  {
   // Native providers can report failed directory enumeration as Warning. A cloud
   // task is successful only when its own structured backup result is Success.
   var started=Date(Get(result.RootElement,"TaskStarted"));var finished=Date(Get(result.RootElement,"TaskFinished"));
   if(started is null||finished is null)return new("Indeterminate",id,"DispatchIndeterminate");
   using var logs=await client.GetAsync($"/api/v1/backup/{Uri.EscapeDataString(cloud.LocalJobId)}/log?pagesize=100",ct);logs.EnsureSuccessStatusCode();
   using var entries=JsonDocument.Parse(await logs.Content.ReadAsStreamAsync(ct));
   foreach(var entry in entries.RootElement.EnumerateArray())
   {
    if(Get(entry,"Type").ToString()!="Result"||Get(entry,"Message").GetString() is not {} message)continue;
    try
    {
     using var payload=JsonDocument.Parse(message);var run=ParseResult(payload.RootElement,default);
     if(run is null||run.Started<started.Value.AddSeconds(-3)||run.Started>finished.Value.AddSeconds(3))continue;
     return run.Status==RunStatus.Success?new("Completed",id,null):new("Failed",id,"EngineTaskFailed");
    }catch(JsonException){}
   }
   // The engine can report the task finished a moment before its result record is readable (seen on fast
   // failing tasks); keep waiting briefly instead of settling on an indeterminate outcome.
   return finished.Value>DateTimeOffset.UtcNow.AddMinutes(-1)?new("Accepted",id,null):new("Indeterminate",id,"DispatchIndeterminate");
  }
  return status switch {"Completed"=>new("Completed",id,null),"Failed"=>new("Failed",id,EngineErrors.Classify(errorMessage,exception)),_=>new("Accepted",id,null)};
 }
 public static string RestoreDestination(string? root,string folder)
 {
  if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||!CommandProtocol.SafeFolder(folder))throw new InvalidOperationException("Local restore root required");
  var full=Path.GetFullPath(root);if(!Directory.Exists(full))throw new InvalidOperationException("Restore root must be provisioned locally");
  for(DirectoryInfo? parent=new DirectoryInfo(full);parent is not null;parent=parent.Parent)if((parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Restore root cannot contain links");
  RestoreRootSecurity.Validate(full);
  var destination=Path.Combine(full,folder);
  // Each restore goes into a new directory; never overwrite originals or traverse existing junctions.
  if(Directory.Exists(destination)||File.Exists(destination))throw new InvalidOperationException("Restore destination already exists");
  return destination;
 }
}

public sealed record CloudTaskBinding(string LocalJobId,DateTimeOffset Dispatched);
// Refused: HTTP status with which the Console permanently refused this receipt (unknown, conflicting or deleted
// command). The entry stays in the journal for replay protection but no longer blocks other commands.
public sealed record CommandJournalEntry(DeviceCommand Command,string Digest,Guid EngineInstance,CommandReceipt? Receipt,bool Reported,int? Refused=null);
public enum ReceiptDelivery { Delivered, Transient, Refused }
public static class RemoteCommands
{
 // 400/404/409/410/422 will never be accepted on retry; 401/403 mean this agent may not talk to the Console at all.
 public static bool PermanentlyRefused(System.Net.HttpStatusCode code)=>(int)code is 400 or 404 or 409 or 410 or 422;
 static async Task<(ReceiptDelivery Delivery,int Code)> Deliver(HttpClient console,Guid id,CommandReceipt receipt,CancellationToken ct)
 {
  try
  {
   using var response=await console.PostAsJsonAsync($"/api/v1/agent/commands/{id}/receipt",receipt,ct);
   if(response.IsSuccessStatusCode)return (ReceiptDelivery.Delivered,(int)response.StatusCode);
   if(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)throw new HttpRequestException("Console refused the agent credential",null,response.StatusCode);
   return (PermanentlyRefused(response.StatusCode)?ReceiptDelivery.Refused:ReceiptDelivery.Transient,(int)response.StatusCode);
  }
  catch(HttpRequestException ex)when(ex.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)){return (ReceiptDelivery.Transient,0);}
  catch(TaskCanceledException)when(!ct.IsCancellationRequested){return (ReceiptDelivery.Transient,0);}
 }
 // Records the delivery outcome. A transient failure keeps the entry unreported so it is retried next cycle,
 // while the remaining entries and new commands are still processed.
 static void Record(Dictionary<Guid,CommandJournalEntry> journal,ProtectedState state,Guid id,(ReceiptDelivery Delivery,int Code) outcome,Action<string>? log)
 {
  if(outcome.Delivery==ReceiptDelivery.Delivered)journal[id]=journal[id] with{Reported=true};
  else if(outcome.Delivery==ReceiptDelivery.Refused){journal[id]=journal[id] with{Reported=true,Refused=outcome.Code};log?.Invoke($"Console refused the receipt of a {journal[id].Command.Action} command with HTTP {outcome.Code}; the command is not executed again");}
  state.Write("commands.bin",journal);
 }
 public static async Task Synchronize(HttpClient console,DuplicatiAdapter adapter,AgentOptions options,ProtectedState state,Guid device,CancellationToken ct,Action<string>? log=null)
 {
  if(!options.AllowRemoteCommands)return;
  using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(options.CommandPublicKeyFile!));if(key.KeySize!=256)throw new CryptographicException("Pinned key must be ECDSA P-256");
  var journal=state.Read<Dictionary<Guid,CommandJournalEntry>>("commands.bin")??[];
  // Complete the write-ahead journal before requesting more work. A crash during dispatch is never retried blindly.
  foreach(var (id,entry) in journal.ToArray())
  {
   if(entry.Refused is not null||entry.Reported&&entry.Receipt?.Status!="Accepted")continue;
   var receipt=entry.Receipt??new CommandReceipt("Indeterminate",null,"DispatchIndeterminate");
   if(receipt.Status=="Accepted"&&receipt.TaskId is {} task)
   {
    if(entry.Command.Action==RemoteAction.RestoreProxmox&&ProxmoxRestore.IndependentReceipt(state,id) is {} independent)receipt=independent;
    else if(state.Read<Guid>("engine-instance.bin")!=entry.EngineInstance)receipt=new("Indeterminate",task,"DispatchIndeterminate");
    else
    try{receipt=entry.Command.Action==RemoteAction.RestoreProxmox?await adapter.ProxmoxReceipt(id,task,ct):await adapter.TaskReceipt(task,ct);if(state.Read<Guid>("engine-instance.bin")!=entry.EngineInstance)receipt=new("Indeterminate",task,"DispatchIndeterminate");}
    catch(HttpRequestException){continue;} // Preserve accepted task; do not mistake missing history for success.
   }
   journal[id]=entry with{Receipt=receipt};state.Write("commands.bin",journal);
   Record(journal,state,id,await Deliver(console,id,receipt,ct),log);
  }
  var envelopes=await console.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands",ct)??[];
  foreach(var envelope in envelopes)
  {
   var command=CommandProtocol.Verify(envelope,key,device,DateTimeOffset.UtcNow);var digest=Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(envelope.Payload)));
   if(journal.TryGetValue(command.Id,out var old))
   {
    if(old.Digest!=digest)throw new CryptographicException("Command mutation rejected");
    // Replay protection: a known command is never executed twice, only its recorded outcome is reported again.
    if(old.Refused is null)Record(journal,state,command.Id,await Deliver(console,command.Id,old.Receipt??new("Indeterminate",null,"DispatchIndeterminate"),ct),log);
    continue;
   }
   var instance=state.Read<Guid>("engine-instance.bin");if(instance==Guid.Empty)throw new InvalidOperationException("Engine instance identity is required");
   journal[command.Id]=new(command,digest,instance,null,false);state.Write("commands.bin",journal);
   CommandReceipt receipt;
   try
   {
    receipt=command.Action switch
    {
     RemoteAction.ListRestorePoints or RemoteAction.ListRestoreFiles=>new("Completed",null,null,await adapter.ReadCatalog(command,ct)),
     RemoteAction.BrowseFolders=>new("Completed",null,null,await adapter.BrowseFolders(command,ct)),
     RemoteAction.TestDestination=>await adapter.TestDestination(command,ct),
     _=>new("Accepted",await adapter.Dispatch(command,options,ct),null),
    };
   }
   catch(InvalidOperationException){receipt=new("Rejected",null,"PolicyRejected");}
   catch(HttpRequestException){receipt=new("Indeterminate",null,"DispatchIndeterminate");}
   if(state.Read<Guid>("engine-instance.bin")!=instance)receipt=new("Indeterminate",receipt.TaskId,"DispatchIndeterminate");
   journal[command.Id]=new(command,digest,instance,receipt,false);state.Write("commands.bin",journal);
   Record(journal,state,command.Id,await Deliver(console,command.Id,receipt,ct),log);
  }
  foreach(var id in journal.Where(x=>x.Value.Reported&&(x.Value.Refused is not null||x.Value.Receipt?.Status!="Accepted")&&x.Value.Command.Expires<DateTimeOffset.UtcNow.AddDays(-1)).Select(x=>x.Key).ToArray())journal.Remove(id);
  state.Write("commands.bin",journal);
 }
}
