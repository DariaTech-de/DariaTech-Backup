using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using DariaTech.Contracts;
namespace DariaTech.Agent;
public sealed class Worker(AgentOptions options,ProtectedState state,ILogger<Worker> log):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken ct)
 {
  options.Validate();var identity=state.Read<AgentIdentity>("identity.bin")??throw new InvalidOperationException("Agent must be enrolled before service startup");
  using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(options.ConsoleUrl),Timeout=TimeSpan.FromSeconds(20)};
  client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",identity.Credential);client.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var adapter=new DuplicatiAdapter(options,state);
  var queue=state.Read<List<HeartbeatRequest>>("outbox.bin")??[];
  using var timer=new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatSeconds));
  do
  {
   try
   {
    JobReport[] jobs=[];ProgressReport? progress=null;var reachable=true;
    try{jobs=await adapter.ReadJobs(ct);progress=await adapter.ReadProgress(ct);if(progress is not null&&jobs.All(x=>x.LocalId!=progress.LocalJobId))progress=null;}catch(Exception ex)when(ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException){reachable=false;log.LogWarning("Local engine unavailable ({Type})",ex.GetType().Name);}
    queue.Add(new(options.Version,RuntimeInformation.OSDescription,reachable,jobs,reachable?progress:null));
    // Bound disk use. Lost older snapshots are explicitly reported, never claimed delivered.
    if(queue.Count>1440){queue.RemoveAt(0);log.LogWarning("Telemetry outbox full; oldest snapshot discarded");}
    state.Write("outbox.bin",queue);
    while(queue.Count>0)
    {
     using var result=await client.PostAsJsonAsync("/api/v1/agent/heartbeat",queue[0],ct);
     if(!result.IsSuccessStatusCode){log.LogWarning("Console rejected telemetry with HTTP {Code}",(int)result.StatusCode);break;}
     queue.RemoveAt(0);state.Write("outbox.bin",queue);
    }
   }
   catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
   catch(Exception ex){log.LogWarning("Telemetry cycle failed ({Type})",ex.GetType().Name);}
  }while(await timer.WaitForNextTickAsync(ct));
 }
}
