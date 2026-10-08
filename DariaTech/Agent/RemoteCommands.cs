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
  HttpResponseMessage response;
  if(command.Action==RemoteAction.StopBackup)
  {
   var progress=await ReadProgress(ct);if(progress is null||progress.LocalJobId!=command.LocalJobId||progress.TaskId<1)throw new InvalidOperationException("No matching active task");
   using var stopped=await client.PostAsync($"/api/v1/task/{progress.TaskId}/stop",null,ct);stopped.EnsureSuccessStatusCode();return progress.TaskId;
  }
  if(command.Action==RemoteAction.Restore)
  {
   var restore=command.Restore!;var destination=RestoreDestination(options.RestoreRoot,restore.DestinationFolder);
   response=await client.PostAsJsonAsync($"/api/v1/backup/{command.LocalJobId}/restore",new{paths=restore.Paths,time=restore.Snapshot.ToUniversalTime().ToString("O"),restore_path=destination,overwrite=false,permissions=false,skip_metadata=true},ct);
  }
  else response=await client.PostAsync($"/api/v1/backup/{command.LocalJobId}/{(command.Action==RemoteAction.RunBackup?"run":"verify")}",null,ct);
  using(response){response.EnsureSuccessStatusCode();using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));if(!long.TryParse(Get(result.RootElement,"ID").ToString(),out var id)||id<1)throw new HttpRequestException("Invalid engine task receipt");return id;}
 }
 public async Task<CommandReceipt> TaskReceipt(long id,CancellationToken ct)
 {
  using var response=await client.GetAsync($"/api/v1/task/{id}",ct);response.EnsureSuccessStatusCode();using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  var status=Get(result.RootElement,"Status").GetString();return status switch {"Completed"=>new("Completed",id,null),"Failed"=>new("Failed",id,"EngineTaskFailed"),_=>new("Accepted",id,null)};
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

public sealed record CommandJournalEntry(DeviceCommand Command,string Digest,Guid EngineInstance,CommandReceipt? Receipt,bool Reported);
public static class RemoteCommands
{
 public static async Task Synchronize(HttpClient console,DuplicatiAdapter adapter,AgentOptions options,ProtectedState state,Guid device,CancellationToken ct)
 {
  if(!options.AllowRemoteCommands)return;
  using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(options.CommandPublicKeyFile!));if(key.KeySize!=256)throw new CryptographicException("Pinned key must be ECDSA P-256");
  var journal=state.Read<Dictionary<Guid,CommandJournalEntry>>("commands.bin")??[];
  // Complete the write-ahead journal before requesting more work. A crash during dispatch is never retried blindly.
  foreach(var (id,entry) in journal.ToArray())
  {
   if(entry.Reported&&entry.Receipt?.Status!="Accepted")continue;
   var receipt=entry.Receipt??new CommandReceipt("Indeterminate",null,"DispatchIndeterminate");
   if(receipt.Status=="Accepted"&&receipt.TaskId is {} task)
   {
    if(state.Read<Guid>("engine-instance.bin")!=entry.EngineInstance)receipt=new("Indeterminate",task,"DispatchIndeterminate");
    else
    try{receipt=await adapter.TaskReceipt(task,ct);if(state.Read<Guid>("engine-instance.bin")!=entry.EngineInstance)receipt=new("Indeterminate",task,"DispatchIndeterminate");}
    catch(HttpRequestException){continue;} // Preserve accepted task; do not mistake missing history for success.
   }
   journal[id]=entry with{Receipt=receipt};state.Write("commands.bin",journal);
   using var response=await console.PostAsJsonAsync($"/api/v1/agent/commands/{id}/receipt",receipt,ct);response.EnsureSuccessStatusCode();journal[id]=journal[id] with{Reported=true};state.Write("commands.bin",journal);
  }
  var envelopes=await console.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands",ct)??[];
  foreach(var envelope in envelopes)
  {
   var command=CommandProtocol.Verify(envelope,key,device,DateTimeOffset.UtcNow);var digest=Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(envelope.Payload)));
   if(journal.TryGetValue(command.Id,out var old))
   {
    if(old.Digest!=digest)throw new CryptographicException("Command mutation rejected");
    using var replay=await console.PostAsJsonAsync($"/api/v1/agent/commands/{command.Id}/receipt",old.Receipt??new("Indeterminate",null,"DispatchIndeterminate"),ct);replay.EnsureSuccessStatusCode();continue;
   }
   var instance=state.Read<Guid>("engine-instance.bin");if(instance==Guid.Empty)throw new InvalidOperationException("Engine instance identity is required");
   journal[command.Id]=new(command,digest,instance,null,false);state.Write("commands.bin",journal);
   CommandReceipt receipt;
   try{receipt=command.Action is RemoteAction.ListRestorePoints or RemoteAction.ListRestoreFiles?new("Completed",null,null,await adapter.ReadCatalog(command,ct)):new("Accepted",await adapter.Dispatch(command,options,ct),null);}
   catch(InvalidOperationException){receipt=new("Rejected",null,"PolicyRejected");}
   catch(HttpRequestException){receipt=new("Indeterminate",null,"DispatchIndeterminate");}
   if(state.Read<Guid>("engine-instance.bin")!=instance)receipt=new("Indeterminate",receipt.TaskId,"DispatchIndeterminate");
   journal[command.Id]=new(command,digest,instance,receipt,false);state.Write("commands.bin",journal);
   using var response=await console.PostAsJsonAsync($"/api/v1/agent/commands/{command.Id}/receipt",receipt,ct);response.EnsureSuccessStatusCode();journal[command.Id]=journal[command.Id] with{Reported=true};state.Write("commands.bin",journal);
  }
  foreach(var id in journal.Where(x=>x.Value.Reported&&x.Value.Receipt?.Status!="Accepted"&&x.Value.Command.Expires<DateTimeOffset.UtcNow.AddDays(-1)).Select(x=>x.Key).ToArray())journal.Remove(id);
  state.Write("commands.bin",journal);
 }
}
