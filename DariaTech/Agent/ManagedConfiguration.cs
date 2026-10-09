using System.Net.Http.Json;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed partial class DuplicatiAdapter
{
 public async Task<string> ApplyConfiguration(ConfigurationAssignment assignment,CancellationToken ct)
 {
  if(!ConfigurationPolicy.Valid(assignment.Definition))throw new InvalidOperationException("Configuration rejected");
  var restoreOnly=assignment.Definition.RestoreOnly==true;
  // A recovery copy never reads its sources (they belong to the original device), so they are not authorized here.
  if(!restoreOnly)SourceChecks.AuthorizeFiles(agentOptions,assignment.Definition.Sources);
  if(assignment.Definition.Saas is {} source)await RequireSaasProvider(source,false,ct);
  var tag=$"DariaTechManaged:{assignment.JobId:D}";
  // A taken-over job waits for its database rebuild before anything else changes (the rebuild is an engine task).
  if(await FindManaged(tag,ct) is {Length:>0} existing&&!await AdoptionFinished(existing,ct))throw new AdoptionPendingException();
  if(await HasPendingTasks(ct))throw new InvalidOperationException("Configuration cannot change while engine tasks are active");
  using var list=await client.GetAsync("/api/v1/backups",ct);list.EnsureSuccessStatusCode();
  using var doc=JsonDocument.Parse(await list.Content.ReadAsStreamAsync(ct));
  var matches=doc.RootElement.EnumerateArray().Where(x=>Get(Get(x,"Backup"),"Tags") is var t&&t.ValueKind==JsonValueKind.Array&&t.EnumerateArray().Any(v=>v.GetString()==tag)).ToArray();
  if(matches.Length>1)throw new InvalidOperationException("Duplicate managed identity");
  var old=matches.Length==1?Get(matches[0],"Backup"):default;
  var id=Get(old,"ID").ToString();
  // A taken-over backup without a local database (new engine job, or one whose rebuild never started) gets no
  // schedule until the database has been rebuilt from the destination.
  var adopting=assignment.Definition.Adopted==true&&(string.IsNullOrEmpty(id)||Get(old,"DBPathExists").ValueKind==JsonValueKind.False);
  var definition=assignment.Definition;
  // No script options, database paths, custom modules or arbitrary engine API supplied by the Console.
  // The engine unmask/update path requires unique option names, including options
  // shared by local and SaaS jobs. Keep one value per case-insensitive engine key.
  var settings=new Dictionary<string,string>(definition.BackendOptions,StringComparer.OrdinalIgnoreCase)
  {
   ["encryption-module"]="aes",["passphrase"]=definition.Passphrase,
   ["keep-versions"]=definition.KeepVersions.ToString(System.Globalization.CultureInfo.InvariantCulture),
   ["abort-if-source-missing"]="true",["disable-module"]="console-password-input"
  };
  var mountPoint=Path.Combine(Path.GetPathRoot(Path.GetFullPath(agentOptions.StateDirectory))!,"DariaTechCloud",assignment.JobId.ToString("D"));
  var sources=definition.Sources;
  if(definition.Saas is {} saas)
  {
   foreach(var option in SaasPolicy.Options(saas))settings[option.Key]=option.Value;
   sources=["@"+mountPoint+"|"+SaasPolicy.Key(saas.Provider)+"://"];
  }
  var requiredMounts=SourceChecks.RequiredMounts(agentOptions,definition);
  if(definition.Proxmox is {} guests)
  {
   if(definition.Filters.Length>0)throw new InvalidOperationException("Image archives cannot be partially excluded");
   sources=[ProxmoxWorkloads.Authorize(agentOptions,assignment.JobId,guests)+Path.DirectorySeparatorChar];
  }
  if(requiredMounts.Length>0||definition.Proxmox is not null||agentOptions.AllowProxmoxSnapshots&&definition.Saas is null)
  {
   var binding=new ManagedSourceBinding(assignment.JobId,assignment.Revision,sources,requiredMounts,definition.Proxmox);
   // Immutable revision in the hook prevents a failed engine PUT from changing the old job's preflight.
   SourceChecks.Bind(state,binding);
   settings["run-script-before-required"]=SourceChecks.Hook(agentOptions,assignment.JobId,assignment.Revision);
   settings["run-script-with-arguments"]="true";
   settings["run-script-timeout"]=definition.Proxmox is null?"60s":"0s";
   settings["symlink-policy"]="ignore";
  }
  var previousSchedule=matches.Length==1?Get(matches[0],"Schedule"):default;
  var schedule=!adopting&&definition.Schedule is {} sc?new {ID=Get(previousSchedule,"ID").ValueKind==JsonValueKind.Number&&Get(previousSchedule,"ID").TryGetInt64(out var sid)?sid:0L,Time=sc.Start.UtcDateTime,Repeat=$"{sc.RepeatHours}h",AllowedDays=sc.Days.Select(x=>x.ToString()).ToArray()}:null;
  var input=new{Backup=new{Name=definition.Name,TargetURL=definition.TargetUrl,Sources=sources,Settings=settings.Select(x=>new{Name=x.Key,Value=x.Value}),
   Tags=restoreOnly?new[]{tag,$"DariaTechRevision:{assignment.Revision}",RestoreOnlyTag}:new[]{tag,$"DariaTechRevision:{assignment.Revision}"},Metadata=Get(old,"Metadata").ValueKind==JsonValueKind.Object?Get(old,"Metadata"):JsonSerializer.SerializeToElement(new{}),
   Filters=definition.Filters.Select((x,i)=>new{Order=i,x.Include,x.Expression})},Schedule=schedule};
  using var response=string.IsNullOrEmpty(id)?await client.PostAsJsonAsync("/api/v1/backups",input,ct):await client.PutAsJsonAsync($"/api/v1/backup/{Uri.EscapeDataString(id)}",input,ct);
  response.EnsureSuccessStatusCode();
  if(!string.IsNullOrEmpty(id))
  {
   BindSaasJob(id,assignment,mountPoint);BindSourceJob(id,assignment);BindRestoreOnly(id,restoreOnly);
   if(adopting){await StartAdoption(id,assignment.JobId,ct);throw new AdoptionPendingException();}
   return id;
  }
  // Reconcile using stable engine tags rather than an in-memory response; safe after a service crash.
  using var result=await client.GetAsync("/api/v1/backups",ct);result.EnsureSuccessStatusCode();using var updated=JsonDocument.Parse(await result.Content.ReadAsStreamAsync(ct));
  var assignedId=updated.RootElement.EnumerateArray().Where(x=>Get(Get(x,"Backup"),"Tags") is var t&&t.ValueKind==JsonValueKind.Array&&t.EnumerateArray().Any(v=>v.GetString()==tag)).Select(x=>Get(Get(x,"Backup"),"ID").ToString()).Single();
  BindSaasJob(assignedId,assignment,mountPoint);BindSourceJob(assignedId,assignment);BindRestoreOnly(assignedId,restoreOnly);
  if(adopting){await StartAdoption(assignedId,assignment.JobId,ct);throw new AdoptionPendingException();}
  return assignedId;
 }
 private async Task<string?> FindManaged(string tag,CancellationToken ct)
 {
  using var list=await client.GetAsync("/api/v1/backups",ct);list.EnsureSuccessStatusCode();
  using var doc=JsonDocument.Parse(await list.Content.ReadAsStreamAsync(ct));
  return doc.RootElement.EnumerateArray().Select(x=>Get(x,"Backup")).Where(x=>Get(x,"Tags") is var t&&t.ValueKind==JsonValueKind.Array&&t.EnumerateArray().Any(v=>v.GetString()==tag)).Select(x=>Get(x,"ID").ToString()).FirstOrDefault();
 }
 public const string RestoreOnlyTag="DariaTechRestoreOnly";
 // Local journal of recovery copies; RunBackup is refused for them even if the Console asked for it.
 private void BindRestoreOnly(string localId,bool restoreOnly)
 {
  var jobs=state.Read<HashSet<string>>("restore-only-jobs.bin")??[];
  if(restoreOnly?jobs.Add(localId):jobs.Remove(localId))state.Write("restore-only-jobs.bin",jobs);
 }
 internal bool IsRestoreOnly(string localId)=>(state.Read<HashSet<string>>("restore-only-jobs.bin")??[]).Contains(localId);
 private void BindSourceJob(string localId,ConfigurationAssignment assignment)
 {
  var jobs=state.Read<Dictionary<string,string>>("source-jobs.bin")??[];
  if(assignment.Definition.Proxmox is not null||SourceChecks.RequiredMounts(agentOptions,assignment.Definition).Length>0)jobs[localId]=SourceChecks.BindingKey(assignment.JobId,assignment.Revision);
  else jobs.Remove(localId);
  state.Write("source-jobs.bin",jobs);
 }
}

public sealed record ConfigurationJournalEntry(long Revision,string Digest,ConfigurationReceipt? Receipt);
public static class ManagedConfiguration
{
 public static async Task Synchronize(HttpClient console,DuplicatiAdapter adapter,AgentOptions options,ProtectedState state,CancellationToken ct)
 {
  if(!options.AllowManagedConfiguration)return;
  var assignments=await console.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations",ct)??[];
  var journal=state.Read<Dictionary<Guid,ConfigurationJournalEntry>>("configuration-journal.bin")??[];
  foreach(var assignment in assignments)
  {
   if(assignment.JobId==Guid.Empty||assignment.Revision<1)throw new InvalidOperationException("Invalid configuration revision");
   var digest=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assignment.Definition)));
   journal.TryGetValue(assignment.JobId,out var previous);
   if(previous is not null&&(assignment.Revision<previous.Revision||assignment.Revision==previous.Revision&&digest!=previous.Digest))throw new InvalidOperationException("Configuration rollback or mutation rejected");
   ConfigurationReceipt receipt;
   if(previous is not null&&assignment.Revision==previous.Revision&&previous.Receipt is {Status:"Applied"} applied)receipt=applied;
   else
   {
    journal[assignment.JobId]=new(assignment.Revision,digest,null);state.Write("configuration-journal.bin",journal);
    if(!ConfigurationPolicy.Valid(assignment.Definition))receipt=new(assignment.Revision,null,"Rejected");
    else
    {
     try{receipt=new(assignment.Revision,await adapter.ApplyConfiguration(assignment,ct),"Applied");}
     // Still rebuilding a taken-over job's database: no receipt yet, the assignment is applied again next cycle.
     catch(AdoptionPendingException){continue;}
     catch(HttpRequestException){receipt=new(assignment.Revision,null,"Failed");}
     catch(InvalidOperationException){receipt=new(assignment.Revision,null,"Rejected");}
    }
    journal[assignment.JobId]=new(assignment.Revision,digest,receipt);state.Write("configuration-journal.bin",journal);
   }
   using var response=await console.PostAsJsonAsync($"/api/v1/agent/configurations/{assignment.JobId}/receipt",receipt,ct);response.EnsureSuccessStatusCode();
  }
 }
}
