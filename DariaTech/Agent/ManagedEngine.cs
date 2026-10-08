using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DariaTech.Contracts;
namespace DariaTech.Agent;

// Installer-owned engine only; never adopts an existing Duplicati database/service.
public sealed class ManagedEngine(AgentOptions options,ProtectedState state,ILogger<ManagedEngine> log):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken ct)
 {
  if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Installer-managed engine requires Windows");
  var credential=state.Read<string>("engine-credential.bin");
  if(credential is null)
  {
   var input=Path.Combine(options.StateDirectory,"engine-password.txt");
   credential=File.Exists(input)?File.ReadAllText(input).TrimEnd('\r','\n'):Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
   if(credential.Length<14||credential.Length>200)throw new InvalidOperationException("Local engine password must be 14–200 characters");
   state.Write("engine-credential.bin",credential);if(File.Exists(input))File.Delete(input);
  }
  var key=state.Read<string>("engine-key.bin");
  if(key is null){key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));state.Write("engine-key.bin",key);}
  if(state.Read<AgentIdentity>("identity.bin") is null)
  {
   var input=Path.Combine(options.StateDirectory,"enrollment-token.txt");
   if(!File.Exists(input))throw new InvalidOperationException("Enrollment token file required for first startup");
   var token=File.ReadAllText(input).Trim();
   using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(options.ConsoleUrl),Timeout=TimeSpan.FromSeconds(30)};
   using var response=await client.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(token,Environment.MachineName,RuntimeInformation.OSDescription,options.Version),ct);
   if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Enrollment rejected with HTTP {(int)response.StatusCode}");
   var identity=await response.Content.ReadFromJsonAsync<EnrollmentResponse>(ct)??throw new InvalidOperationException("Missing enrollment response");
   state.Write("identity.bin",new AgentIdentity(identity.DeviceId,identity.Credential));File.Delete(input);
  }
  // A failed installer attempt may have left an already-consumed input file.
  File.Delete(Path.Combine(options.StateDirectory,"enrollment-token.txt"));
  File.Delete(Path.Combine(options.StateDirectory,"engine-password.txt"));
  var executable=Path.Combine(AppContext.BaseDirectory,"engine","Duplicati.Server.exe");
  if(!File.Exists(executable))throw new InvalidOperationException("Bundled engine missing");
  // Duplicati creates its own leaf directory with canonical, non-inherited ACLs.
  // Pre-creating it here would correctly be rejected as an insecure existing folder.
  var data=Path.Combine(options.StateDirectory,"engine");
  while(!ct.IsCancellationRequested)
  {
   using var process=new Process{StartInfo=CreateStartInfo(executable,data,options.EngineUrl,credential,key)};
   // Child logs may include customer paths. Consume locally without forwarding/logging them.
   process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};
   var started=false;
   try
   {
    state.Write("engine-instance.bin",Guid.NewGuid());
    process.Start();started=true;using var job=WindowsJob.Attach(process);
    state.Write("engine-process.bin",new EngineProcessIdentity(process.Id,process.StartTime.ToUniversalTime().Ticks));
    process.BeginOutputReadLine();process.BeginErrorReadLine();
    await process.WaitForExitAsync(ct);log.LogWarning("Bundled engine exited with code {Code}; restarting",process.ExitCode);
   }
   finally{if(started&&!process.HasExited){process.Kill(true);await process.WaitForExitAsync(CancellationToken.None);}}
   await Task.Delay(TimeSpan.FromSeconds(10),ct);
  }
 }
 public static ProcessStartInfo CreateStartInfo(string executable,string data,string engineUrl,string credential,string key)
 {
  var uri=new Uri(engineUrl);
  if(!uri.IsLoopback||uri.Scheme!="http")throw new InvalidOperationException("Managed engine requires HTTP loopback");
  var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(executable)!};
  foreach(var arg in new[]{"--webservice-interface=loopback",$"--webservice-port={uri.Port}",$"--server-datafolder={data}","--disable-update-check=true","--require-db-encryption-key=true","--webservice-allowed-hostnames=localhost,127.0.0.1","--webservice-suppress-welcome-page=true"})start.ArgumentList.Add(arg);
  start.Environment["DUPLICATI__WEBSERVICE_PASSWORD"]=credential;
  start.Environment["SETTINGS_ENCRYPTION_KEY"]=key;
  start.Environment["DO_NOT_TRACK"]="1";start.Environment["USAGEREPORTER_Duplicati_LEVEL"]="none";start.Environment["AUTOUPDATER_Duplicati_SKIP_UPDATE"]="1";
  return start;
 }
}
