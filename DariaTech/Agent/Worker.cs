using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using DariaTech.Contracts;
namespace DariaTech.Agent;
public sealed class Worker(AgentOptions options,ProtectedState state,ILogger<Worker> log):BackgroundService
{
 private const int CommandPollSeconds=5;
 protected override async Task ExecuteAsync(CancellationToken ct)
 {
  options.Validate();
  while(options.ManageEngine&&state.Read<AgentIdentity>("identity.bin") is null)await Task.Delay(TimeSpan.FromSeconds(2),ct);
  var identity=state.Read<AgentIdentity>("identity.bin")??throw new InvalidOperationException("Agent must be enrolled before service startup");
  using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(options.ConsoleUrl),Timeout=TimeSpan.FromSeconds(20)};
  client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",identity.Credential);client.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var adapter=new DuplicatiAdapter(options,state);
  var queue=state.Read<List<HeartbeatRequest>>("outbox.bin")??[];
  // Remote commands are polled every few seconds so folder browsing, connection tests and restores answer
  // promptly; the full telemetry cycle still runs once per heartbeat interval.
  var tick=options.AllowRemoteCommands?TimeSpan.FromSeconds(Math.Min(CommandPollSeconds,options.HeartbeatSeconds)):TimeSpan.FromSeconds(options.HeartbeatSeconds);
  using var timer=new PeriodicTimer(tick);
  var lastCycle=DateTimeOffset.MinValue;
  do
  {
   if(DateTimeOffset.UtcNow-lastCycle<TimeSpan.FromSeconds(options.HeartbeatSeconds))
   {
    var update=state.Read<UpdateJournal>("update.bin");if(update is {Status:"Applying"}&&update.Manifest.Version!=options.Version)continue;
    try{await RemoteCommands.Synchronize(client,adapter,options,state,identity.DeviceId,ct);}
    catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
    catch(Exception ex){log.LogDebug("Remote command poll failed ({Type})",ex.GetType().Name);}
    continue;
   }
   lastCycle=DateTimeOffset.UtcNow;
   try
   {
    JobReport[] jobs=[];ProgressReport? progress=null;var reachable=true;
    try{jobs=await adapter.ReadJobs(ct);progress=await adapter.ReadProgress(ct);if(progress is not null&&jobs.All(x=>x.LocalId!=progress.LocalJobId))progress=null;}catch(Exception ex)when(ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException){reachable=false;log.LogWarning("Local engine unavailable ({Type})",ex.GetType().Name);}
    var update=state.Read<UpdateJournal>("update.bin");var updating=update is {Status:"Applying"}&&update.Manifest.Version!=options.Version;
    if(reachable&&!updating&&state.Read<EnginePauseState>("update-preserve-pause.bin") is {Paused:true} pause)
    {
     if(pause.Until is null||pause.Until>DateTimeOffset.UtcNow)await adapter.SetPause(true,ct,pause.Until,pause.Until is null);
     state.Write("update-preserve-pause.bin",pause with{Paused=false});
    }
    if(reachable&&!updating)
    {
     try{await ManagedConfiguration.Synchronize(client,adapter,options,state,ct);}
     catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
     catch(Exception ex){log.LogWarning("Managed configuration synchronization failed ({Type})",ex.GetType().Name);}
    }
    if(reachable&&!updating)
    {
     try{await RemoteCommands.Synchronize(client,adapter,options,state,identity.DeviceId,ct);}
     catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
     catch(Exception ex){log.LogWarning("Remote command synchronization failed ({Type})",ex.GetType().Name);}
    }
    if(reachable)
    {
     try{await AgentUpdates.Synchronize(client,adapter,options,state,ct);}
     catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
     catch(Exception ex){log.LogWarning("Agent update synchronization failed ({Type})",ex.GetType().Name);}
    }
    queue.Add(new(options.Version,RuntimeInformation.OSDescription,reachable,jobs,reachable?progress:null,options.Platform));
    // Bound disk use. Lost older snapshots are explicitly reported, never claimed delivered.
    if(queue.Count>1440){queue.RemoveAt(0);log.LogWarning("Telemetry outbox full; oldest snapshot discarded");}
    state.Write("outbox.bin",queue);
    var telemetryDelivered=false;
    while(queue.Count>0)
    {
     using var result=await client.PostAsJsonAsync("/api/v1/agent/heartbeat",queue[0],ct);
     if(!result.IsSuccessStatusCode){log.LogWarning("Console rejected telemetry with HTTP {Code}",(int)result.StatusCode);break;}
     queue.RemoveAt(0);state.Write("outbox.bin",queue);telemetryDelivered=true;
    }
    if(reachable&&telemetryDelivered)
    {
     try{await adapter.CaptureHistory(client,jobs,ct);}
     catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
     catch(Exception ex){log.LogWarning("History capture or delivery failed ({Type})",ex.GetType().Name);}
    }
   }
   catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
   catch(Exception ex){log.LogWarning("Telemetry cycle failed ({Type})",ex.GetType().Name);}
  }while(await timer.WaitForNextTickAsync(ct));
 }
}
