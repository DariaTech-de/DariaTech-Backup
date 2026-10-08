using System.Net.Http.Json;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed partial class DuplicatiAdapter
{
 public async Task<string> ApplyConfiguration(ConfigurationAssignment assignment,CancellationToken ct)
 {
  if(!ConfigurationPolicy.Valid(assignment.Definition))throw new InvalidOperationException("Configuration rejected");
  SourceChecks.AuthorizeFiles(agentOptions,assignment.Definition.Sources);
  if(assignment.Definition.Saas is {} source)await RequireSaasProvider(source,false,ct);
  if(await HasPendingTasks(ct))throw new InvalidOperationException("Configuration cannot change while engine tasks are active");
  using var list=await client.GetAsync("/api/v1/backups",ct);list.EnsureSuccessStatusCode();
  using var doc=JsonDocument.Parse(await list.Content.ReadAsStreamAsync(ct));
  var tag=$"DariaTechManaged:{assignment.JobId:D}";
  var matches=doc.RootElement.EnumerateArray().Where(x=>Get(Get(x,"Backup"),"Tags") is var t&&t.ValueKind==JsonValueKind.Array&&t.EnumerateArray().Any(v=>v.GetString()==tag)).ToArray();
  if(matches.Length>1)throw new InvalidOperationException("Duplicate managed identity");
  var old=matches.Length==1?Get(matches[0],"Backup"):default;
  var id=Get(old,"ID").ToString();
  var definition=assignment.Definition;
  // No script options, database paths, custom modules or arbitrary engine API supplied by the Console.
  var settings=definition.BackendOptions.Select(x=>new{Name=x.Key,Value=x.Value}).ToList();
  settings.AddRange(new[]{new{Name="encryption-module",Value="aes"},new{Name="passphrase",Value=definition.Passphrase},
   new{Name="keep-versions",Value=definition.KeepVersions.ToString(System.Globalization.CultureInfo.InvariantCulture)},new{Name="abort-if-source-missing",Value="true"},new{Name="disable-module",Value="console-password-input"}});
  var mountPoint=Path.Combine(Path.GetPathRoot(Path.GetFullPath(agentOptions.StateDirectory))!,"DariaTechCloud",assignment.JobId.ToString("D"));
  var sources=definition.Sources;
  if(definition.Saas is {} saas)
  {
   settings.AddRange(SaasPolicy.Options(saas).Select(x=>new{Name=x.Key,Value=x.Value}));
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
   settings.Add(new{Name="run-script-before-required",Value=SourceChecks.Hook(agentOptions,assignment.JobId,assignment.Revision)});
   settings.Add(new{Name="run-script-with-arguments",Value="true"});
   settings.Add(new{Name="run-script-timeout",Value=definition.Proxmox is null?"60s":"0s"});
   settings.Add(new{Name="symlink-policy",Value="ignore"});
  }
  var previousSchedule=matches.Length==1?Get(matches[0],"Schedule"):default;
  var schedule=definition.Schedule is {} sc?new {ID=Get(previousSchedule,"ID").ValueKind==JsonValueKind.Number&&Get(previousSchedule,"ID").TryGetInt64(out var sid)?sid:0L,Time=sc.Start.UtcDateTime,Repeat=$"{sc.RepeatHours}h",AllowedDays=sc.Days.Select(x=>x.ToString()).ToArray()}:null;
  var input=new{Backup=new{Name=definition.Name,TargetURL=definition.TargetUrl,Sources=sources,Settings=settings,
   Tags=new[]{tag,$"DariaTechRevision:{assignment.Revision}"},Metadata=Get(old,"Metadata").ValueKind==JsonValueKind.Object?Get(old,"Metadata"):JsonSerializer.SerializeToElement(new{}),
   Filters=definition.Filters.Select((x,i)=>new{Order=i,x.Include,x.Expression})},Schedule=schedule};
  using var response=string.IsNullOrEmpty(id)?await client.PostAsJsonAsync("/api/v1/backups",input,ct):await client.PutAsJsonAsync($"/api/v1/backup/{Uri.EscapeDataString(id)}",input,ct);
  response.EnsureSuccessStatusCode();
  if(!string.IsNullOrEmpty(id)){BindSaasJob(id,assignment,mountPoint);BindSourceJob(id,assignment);return id;}
  // Reconcile using stable engine tags rather than an in-memory response; safe after a service crash.
  using var result=await client.GetAsync("/api/v1/backups",ct);result.EnsureSuccessStatusCode();using var updated=JsonDocument.Parse(await result.Content.ReadAsStreamAsync(ct));
  var assignedId=updated.RootElement.EnumerateArray().Where(x=>Get(Get(x,"Backup"),"Tags") is var t&&t.ValueKind==JsonValueKind.Array&&t.EnumerateArray().Any(v=>v.GetString()==tag)).Select(x=>Get(Get(x,"Backup"),"ID").ToString()).Single();
  BindSaasJob(assignedId,assignment,mountPoint);BindSourceJob(assignedId,assignment);return assignedId;
 }
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
     catch(HttpRequestException){receipt=new(assignment.Revision,null,"Failed");}
     catch(InvalidOperationException){receipt=new(assignment.Revision,null,"Rejected");}
    }
    journal[assignment.JobId]=new(assignment.Revision,digest,receipt);state.Write("configuration-journal.bin",journal);
   }
   using var response=await console.PostAsJsonAsync($"/api/v1/agent/configurations/{assignment.JobId}/receipt",receipt,ct);response.EnsureSuccessStatusCode();
  }
 }
}
