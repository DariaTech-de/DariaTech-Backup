using System.Net;
namespace DariaTech.Agent;

// The Console answers 410 Gone when this device was replaced or decommissioned. The device then stops its own
// scheduled backups by pausing the local engine indefinitely, so an old device never keeps writing into a backup
// chain that another device has taken over. Only after the pause is in effect and no backup is running does it
// confirm the stop; the Console revokes this agent on that confirmation. Local job databases are kept.
public sealed record RetiredState(DateTimeOffset Requested,bool Confirmed);
public static class Retirement
{
 public const string StateFile="retired.bin";
 public static bool Requested(HttpStatusCode code)=>code==HttpStatusCode.Gone;
 public static bool Active(ProtectedState state)=>state.Read<RetiredState>(StateFile) is not null;
 public static async Task<bool> Enforce(HttpClient console,DuplicatiAdapter adapter,ProtectedState state,CancellationToken ct,Action<string> log)
 {
  var retired=state.Read<RetiredState>(StateFile)??new(DateTimeOffset.UtcNow,false);state.Write(StateFile,retired);
  try
  {
   await adapter.ReadJobs(ct); // signs in to the engine; a retired agent skips the normal telemetry cycle
   if(await adapter.PauseState(ct) is not {Paused:true,Until:null})await adapter.SetPause(true,ct,null,true);
   if(await adapter.PauseState(ct) is not {Paused:true,Until:null}){log("Device retirement: the backup engine could not be paused; retrying");return false;}
   // A backup that was already running finishes first; the stop is confirmed only once nothing runs.
   if(await adapter.ReadProgress(ct) is not null){log("Device retirement: waiting for the running operation to finish before confirming the stop");return false;}
  }
  catch(Exception ex)when(ex is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException)
  {log($"Device retirement: local engine unavailable ({ex.GetType().Name}); retrying");return false;}
  if(retired.Confirmed)return true;
  try
  {
   using var response=await console.PostAsync("/api/v1/agent/retired",null,ct);
   if(response.IsSuccessStatusCode||response.StatusCode==HttpStatusCode.Unauthorized)
   {
    // 401: already confirmed earlier (the credential is revoked once the Console has the confirmation).
    state.Write(StateFile,retired with{Confirmed=true});log("Device retirement: scheduled backups are paused; stop confirmed to the Console");return true;
   }
   log($"Device retirement: the Console did not accept the confirmation (HTTP {(int)response.StatusCode}); retrying");return false;
  }
  catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException){log($"Device retirement: Console unreachable ({ex.GetType().Name}); retrying");return false;}
 }
}
