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

  var executable=EngineInstallation.Resolve(options);
  var tls=OperatingSystem.IsWindows()?null:EngineTls.Provision(options,state);
  var credential=state.Read<string>("engine-credential.bin");
  if(credential is null)
  {
   var input=Path.Combine(options.StateDirectory,"engine-password.txt");
   if(!OperatingSystem.IsWindows()&&File.Exists(input))UnixPrivatePaths.File(input);
   credential=File.Exists(input)?File.ReadAllText(input).TrimEnd('\r','\n'):Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
   if(credential.Length<14||credential.Length>200)throw new InvalidOperationException("Local engine password must be 14–200 characters");
   state.Write("engine-credential.bin",credential);if(File.Exists(input))File.Delete(input);
  }
  var key=state.Read<string>("engine-key.bin");
  if(key is null){key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));state.Write("engine-key.bin",key);}
  var enrollmentAttempt=0;
  while(state.Read<AgentIdentity>("identity.bin") is null&&!ct.IsCancellationRequested)
  {
   var input=Path.Combine(options.StateDirectory,"enrollment-token.txt");
   if(!File.Exists(input))throw new InvalidOperationException("Enrollment token file required for first startup");
   if(!OperatingSystem.IsWindows())UnixPrivatePaths.File(input);
   var token=File.ReadAllText(input).Trim();
   try
   {
    using var enrollment=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(options.ConsoleUrl),Timeout=TimeSpan.FromSeconds(30)};
    using var response=await enrollment.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(token,Environment.MachineName,RuntimeInformation.OSDescription,options.Version,options.Platform),ct);
    if(!response.IsSuccessStatusCode)log.LogWarning("Enrollment not accepted (HTTP {Code}); retaining protected input for operator correction/retry",(int)response.StatusCode);
    else
    {
     var identity=await response.Content.ReadFromJsonAsync<EnrollmentResponse>(ct)??throw new InvalidOperationException("Missing enrollment response");
     if(identity.DeviceId==Guid.Empty||identity.Credential.Length!=64||!identity.Credential.All(Uri.IsHexDigit))throw new InvalidOperationException("Invalid enrollment identity");
     state.Write("identity.bin",new AgentIdentity(identity.DeviceId,identity.Credential));File.Delete(input);break;
    }
   }
   catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
   catch(Exception error)when(error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
   {log.LogWarning("Enrollment connection unavailable ({Type}); retrying without changing identity or trust",error.GetType().Name);}
   enrollmentAttempt=Math.Min(enrollmentAttempt+1,8);
   await Task.Delay(TimeSpan.FromSeconds(Math.Min(300,Math.Pow(2,enrollmentAttempt))),ct);
  }
  // A failed installer attempt may have left an already-consumed input file.
  File.Delete(Path.Combine(options.StateDirectory,"enrollment-token.txt"));
  File.Delete(Path.Combine(options.StateDirectory,"engine-password.txt"));
  // Duplicati creates its own leaf directory with canonical, non-inherited ACLs.
  // Pre-creating it here would correctly be rejected as an insecure existing folder.
  var data=Path.Combine(options.StateDirectory,"engine");
  if(!OperatingSystem.IsWindows())UnixPrivatePaths.Directory(data,true);
  while(!ct.IsCancellationRequested)
  {
   using var process=new Process{StartInfo=CreateStartInfo(executable,data,options.EngineUrl,credential,key,tls)};
   // Child logs may include customer paths. Consume locally without forwarding/logging them.
   process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};
   var started=false;
   try
   {
    state.Write("engine-instance.bin",Guid.NewGuid());
    process.Start();started=true;using var job=OperatingSystem.IsWindows()?WindowsJob.Attach(process):null;
    state.Write("engine-process.bin",new EngineProcessIdentity(process.Id,process.StartTime.ToUniversalTime().Ticks));
    process.BeginOutputReadLine();process.BeginErrorReadLine();
    await process.WaitForExitAsync(ct);log.LogWarning("Bundled engine exited with code {Code}; restarting",process.ExitCode);
   }
   finally{if(started&&!process.HasExited){process.Kill(true);await process.WaitForExitAsync(CancellationToken.None);}}
   await Task.Delay(TimeSpan.FromSeconds(10),ct);
  }
 }
 public static ProcessStartInfo CreateStartInfo(string executable,string data,string engineUrl,string credential,string key,EngineTlsFile? tls=null)
 {
  var uri=new Uri(engineUrl);
  if(!uri.IsLoopback||uri.Host is not ("127.0.0.1" or "localhost")||uri.Scheme!=(tls is null?"http":"https"))throw new InvalidOperationException("Invalid managed loopback transport");
  var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(executable)!};
  foreach(var arg in new[]{"--webservice-interface=loopback",$"--webservice-port={uri.Port}",$"--server-datafolder={data}","--disable-update-check=true","--require-db-encryption-key=true","--webservice-allowed-hostnames=localhost,127.0.0.1","--webservice-suppress-welcome-page=true",
   // No local Duplicati web interface (and with it no remote-control or update settings): devices are managed
   // from the DariaTech Console only. The agent talks to the engine API.
   "--webservice-api-only=true"})start.ArgumentList.Add(arg);
  if(tls is not null)
  {
   start.ArgumentList.Add("--webservice-sslcertificatefile="+tls.Path);start.ArgumentList.Add("--webservice-disable-https=false");
   start.Environment["DUPLICATI__WEBSERVICE_SSLCERTIFICATEPASSWORD"]=tls.Password;
  }
  start.Environment["DUPLICATI__WEBSERVICE_PASSWORD"]=credential;
  start.Environment["SETTINGS_ENCRYPTION_KEY"]=key;
  start.Environment["DO_NOT_TRACK"]="1";start.Environment["USAGEREPORTER_Duplicati_LEVEL"]="none";start.Environment["AUTOUPDATER_Duplicati_SKIP_UPDATE"]="1";
  return start;
 }
}
