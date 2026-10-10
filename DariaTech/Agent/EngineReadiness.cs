namespace DariaTech.Agent;

public sealed record EngineStartMarker(Guid Instance,DateTimeOffset Started,bool Ready);

// The engine accepts HTTP requests before its startup has finished: it still rewrites its settings database
// after the web server is up, and a request that writes in that window can crash it. The managed engine reports
// "Server has started" on stdout once that work is done; requests to a managed engine wait for that line, or for
// a fixed grace period in case the message is localized or not printed.
public static class EngineReadiness
{
 public static readonly TimeSpan Grace=TimeSpan.FromSeconds(20);
 public static bool IsStartedLine(string? line,int port)=>line is not null&&(line.Contains("Server has started",StringComparison.OrdinalIgnoreCase)
  ||line.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture),StringComparison.Ordinal)&&(line.Contains("started",StringComparison.OrdinalIgnoreCase)||line.Contains("gestartet",StringComparison.OrdinalIgnoreCase)));
 public static void Starting(ProtectedState state,Guid instance)=>state.Write("engine-start.bin",new EngineStartMarker(instance,DateTimeOffset.UtcNow,false));
 public static void Started(ProtectedState state,Guid instance)
 {
  if(state.Read<EngineStartMarker>("engine-start.bin") is {} marker&&marker.Instance==instance&&!marker.Ready)state.Write("engine-start.bin",marker with{Ready=true});
 }
 public static bool Ready(ProtectedState state,DateTimeOffset now)=>state.Read<EngineStartMarker>("engine-start.bin") is not {} marker||marker.Ready||now-marker.Started>=Grace;
 public static async Task Wait(ProtectedState state,CancellationToken ct)
 {
  while(!Ready(state,DateTimeOffset.UtcNow))await Task.Delay(250,ct);
 }
}

public sealed class EngineReadinessHandler(HttpMessageHandler inner,ProtectedState state):DelegatingHandler(inner)
{
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  await EngineReadiness.Wait(state,ct);
  return await base.SendAsync(request,ct);
 }
}
