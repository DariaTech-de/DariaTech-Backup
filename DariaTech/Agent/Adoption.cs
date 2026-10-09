using System.Net.Http.Json;
using System.Text.Json;
namespace DariaTech.Agent;

public sealed class AdoptionPendingException():InvalidOperationException("Backup takeover is still rebuilding the local database");
public sealed record AdoptionState(Guid JobId,long TaskId,DateTimeOffset Started);

// Taking over another device's backup ("replace device"): the destination already holds this job's versions,
// so the engine must rebuild its local database from the destination before the first backup. Until that
// repair has finished the job has no schedule, RunBackup is refused and no configuration receipt is sent.
public sealed partial class DuplicatiAdapter
{
 private static readonly TimeSpan AdoptionRetry=TimeSpan.FromMinutes(30);
 internal bool IsAdopting(string localId)=>(state.Read<Dictionary<string,AdoptionState>>("adoptions.bin")??[]).ContainsKey(localId);

 private async Task StartAdoption(string localId,Guid jobId,CancellationToken ct)
 {
  // A repair against an existing (empty) database would treat the destination's files as unknown, so the
  // engine only ever recreates: any local database of this job is removed first.
  using(var list=await client.GetAsync("/api/v1/backups",ct))
  {
   list.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await list.Content.ReadAsStreamAsync(ct));
   var backup=doc.RootElement.EnumerateArray().Select(x=>Get(x,"Backup")).FirstOrDefault(x=>Get(x,"ID").ToString()==localId);
   var path=Get(backup,"DBPath").ToString();
   if(!string.IsNullOrEmpty(path)&&Path.IsPathFullyQualified(path)&&path.EndsWith(".sqlite",StringComparison.OrdinalIgnoreCase)&&File.Exists(path))File.Delete(path);
  }
  // Recorded before the engine call so an interrupted start is retried rather than forgotten.
  var adoptions=state.Read<Dictionary<string,AdoptionState>>("adoptions.bin")??[];adoptions[localId]=new(jobId,0,DateTimeOffset.UtcNow);state.Write("adoptions.bin",adoptions);
  using var response=await client.PostAsJsonAsync($"/api/v1/backup/{Uri.EscapeDataString(localId)}/repair",new{},ct);response.EnsureSuccessStatusCode();
  using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  if(!long.TryParse(Get(result.RootElement,"ID").ToString(),out var task)||task<1)throw new HttpRequestException("Invalid engine task receipt");
  adoptions[localId]=new(jobId,task,DateTimeOffset.UtcNow);state.Write("adoptions.bin",adoptions);
 }

 // True once the rebuild succeeded, false while it runs. A failed rebuild is reported as a failed configuration
 // (HttpRequestException) and restarted after a pause.
 private async Task<bool> AdoptionFinished(string localId,CancellationToken ct)
 {
  var adoptions=state.Read<Dictionary<string,AdoptionState>>("adoptions.bin")??[];
  if(!adoptions.TryGetValue(localId,out var adoption))return true;
  if(adoption.TaskId==0){await StartAdoption(localId,adoption.JobId,ct);return false;}
  using var response=await client.GetAsync($"/api/v1/task/{adoption.TaskId}",ct);
  string? status=null;var failed=response.StatusCode==System.Net.HttpStatusCode.NotFound;
  if(response.IsSuccessStatusCode)
  {
   using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));status=Get(result.RootElement,"Status").GetString();
   failed=status=="Failed"||status=="Completed"&&(!string.IsNullOrWhiteSpace(Get(result.RootElement,"ErrorMessage").ToString())||!string.IsNullOrWhiteSpace(Get(result.RootElement,"Exception").ToString()));
  }
  else if(!failed)response.EnsureSuccessStatusCode();
  if(status=="Completed"&&!failed){adoptions.Remove(localId);state.Write("adoptions.bin",adoptions);return true;}
  if(!failed)return false;
  if(DateTimeOffset.UtcNow-adoption.Started>AdoptionRetry)await StartAdoption(localId,adoption.JobId,ct);
  throw new HttpRequestException("Backup takeover could not rebuild the local database");
 }
}
