using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public static class UnixUpdates
{
 private static string ExpectedRoot=>OperatingSystem.IsMacOS()?"/Library/Application Support/DariaTechBackup/agent":"/opt/dariatech-backup-agent";
 public static string Artifact(AgentOptions options,AgentUpdateManifest manifest)=>Path.Combine(options.StateDirectory,"updates",manifest.ReleaseId.ToString("D")+".tar.gz");
 public static async Task Launch(AgentOptions options,ProtectedState state,UpdateJournal journal,CancellationToken ct)
 {
  if(OperatingSystem.IsWindows()||UnixPrivatePaths.EffectiveUserId!=0||options.ConfigurationFile is null||Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)!=ExpectedRoot)
   throw new InvalidOperationException("Unix automatic updates require the installed root service and its private configuration");
  Validate(options,state,journal);await AgentUpdates.VerifyArtifact(Artifact(options,journal.Manifest),journal.Manifest,ct);
  var updates=Path.Combine(options.StateDirectory,"updates");UnixPrivatePaths.Directory(updates,true);
  var supervisor=Path.Combine(updates,"supervisor-"+journal.Manifest.ReleaseId.ToString("N"));if(Directory.Exists(supervisor))throw new InvalidOperationException("Supervisor dispatch cannot be repeated");
  UnixPrivatePaths.Directory(supervisor,true);
  // Run outside the main service's cgroup/process group and outside replaced binaries.
  foreach(var file in new DirectoryInfo(AppContext.BaseDirectory).EnumerateFiles())
  {
   UnixPrivatePaths.TrustedFile(file.FullName);var target=Path.Combine(supervisor,file.Name);File.Copy(file.FullName,target);
   File.SetUnixFileMode(target,file.Name=="DariaTech.Agent"?UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute:UnixFileMode.UserRead|UnixFileMode.UserWrite);
  }
  var helper=Path.Combine(supervisor,"DariaTech.Agent");
  if(OperatingSystem.IsMacOS())
  {
   var label=UnixServices.MacLabel+".update."+journal.Manifest.ReleaseId.ToString("N");
   var plist="/Library/LaunchDaemons/"+label+".plist";
   if(File.Exists(plist))throw new InvalidOperationException("Update supervisor already registered");
   var content=new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("plist",new System.Xml.Linq.XAttribute("version","1.0"),new System.Xml.Linq.XElement("dict",
    new System.Xml.Linq.XElement("key","Label"),new System.Xml.Linq.XElement("string",label),
    new System.Xml.Linq.XElement("key","ProgramArguments"),new System.Xml.Linq.XElement("array",new System.Xml.Linq.XElement("string",helper),new System.Xml.Linq.XElement("string","--apply-unix-update"),new System.Xml.Linq.XElement("string",options.ConfigurationFile),new System.Xml.Linq.XElement("string",ExpectedRoot)),
    new System.Xml.Linq.XElement("key","RunAtLoad"),new System.Xml.Linq.XElement("true"),new System.Xml.Linq.XElement("key","KeepAlive"),new System.Xml.Linq.XElement("false"),
    new System.Xml.Linq.XElement("key","AbandonProcessGroup"),new System.Xml.Linq.XElement("false"),new System.Xml.Linq.XElement("key","Umask"),new System.Xml.Linq.XElement("integer","63")))).ToString();
   UnixServices.WriteDefinition(plist,content);
   await Run("/bin/launchctl",["bootstrap","system",plist],ct);
  }
  else await Run("/usr/bin/systemd-run",["--unit="+UnixServices.ServiceName+"-update-"+journal.Manifest.ReleaseId.ToString("N"),"--collect","--property=Type=exec","--property=User=root","--property=UMask=0077",helper,"--apply-unix-update",options.ConfigurationFile,ExpectedRoot],ct);
 }
 public static async Task Apply(string configuration,string installation,CancellationToken ct)
 {
  if(OperatingSystem.IsWindows()||UnixPrivatePaths.EffectiveUserId!=0||Path.GetFullPath(installation).TrimEnd(Path.DirectorySeparatorChar)!=ExpectedRoot)throw new InvalidOperationException("Invalid update supervisor context");
  UnixPrivatePaths.File(configuration);using var config=JsonDocument.Parse(File.ReadAllText(configuration));
  var options=config.RootElement.GetProperty("Agent").Deserialize<AgentOptions>()??throw new InvalidOperationException("Agent configuration missing");options.ConfigurationFile=configuration;options.Validate();
  var state=new ProtectedState(options);var journal=state.Read<UpdateJournal>("update.bin")??throw new InvalidOperationException("Update journal missing");
  if(journal.Status!="Applying")throw new InvalidOperationException("Update is not awaiting installation");
  var previous=installation+".previous";var moved=false;var stopped=false;var pausedByUs=false;var wasPaused=false;
  using var adapter=new DuplicatiAdapter(options,state);
  try
  {
   Validate(options,state,journal);UnixPrivatePaths.Installation(Path.Combine(installation,"engine","Duplicati.Server"));
   await AgentUpdates.VerifyArtifact(Artifact(options,journal.Manifest),journal.Manifest,ct);
   var extracted=Path.Combine(options.StateDirectory,"updates","extract-"+journal.Manifest.ReleaseId.ToString("N"));
   var agent=await UnixUpdatePackage.Extract(Artifact(options,journal.Manifest),extracted,journal.Manifest,ct);
   // Wait for complete native tasks, then freeze scheduling without pausing data transfers.
   // If a task raced into the queue, resume and let it finish; never kill an active backup.
   for(var attempt=0;attempt<900;attempt++)
   {
    await adapter.ReadJobs(ct);
    if(await adapter.HasPendingTasks(ct)){await Task.Delay(1000,ct);continue;}
    wasPaused=await adapter.IsPaused(ct);
    if(!wasPaused){await adapter.SetPause(true,ct);pausedByUs=true;}
    if(!await adapter.HasPendingTasks(ct))break;
    if(pausedByUs){await adapter.SetPause(false,ct);pausedByUs=false;}
    if(attempt==899)throw new InvalidOperationException("Engine remained busy");
   }
   if(await adapter.HasPendingTasks(ct))throw new InvalidOperationException("Engine is not idle");
   if(wasPaused)state.Write("update-preserve-pause.bin",await adapter.PauseState(ct));
   Validate(options,state,journal); // Expiry and offline signature are checked again before replacement.
   await Stop(ct);stopped=true;
   if(Directory.Exists(previous)){UnixPrivatePaths.Installation(Path.Combine(previous,"engine","Duplicati.Server"));Directory.Delete(previous,true);}
   Directory.Move(installation,previous);moved=true;Directory.Move(agent,installation);
   await Start(ct);
   await WaitForEngine(options,state,ct);
   // The new agent confirms its compiled version to the Console; this helper never invents Installed.
  }
  catch
  {
   if(moved)
   {
    try{await Stop(CancellationToken.None);}catch(InvalidOperationException){}
    if(Directory.Exists(installation))Directory.Move(installation,Path.Combine(options.StateDirectory,"updates","failed-"+journal.Manifest.ReleaseId.ToString("N")));
    Directory.Move(previous,installation);
   }
   state.Write("update.bin",journal with{Status="ApplyFailed"});
   if(stopped)await Start(CancellationToken.None);
   if(pausedByUs&&!wasPaused){try{await WaitForEngine(options,state,CancellationToken.None);using var resumed=new DuplicatiAdapter(options,state);await resumed.ReadJobs(CancellationToken.None);await resumed.SetPause(false,CancellationToken.None);}catch(Exception){}}
   throw;
  }
  finally
  {
   if(OperatingSystem.IsMacOS())File.Delete("/Library/LaunchDaemons/"+UnixServices.MacLabel+".update."+journal.Manifest.ReleaseId.ToString("N")+".plist");
  }
 }
 private static void Validate(AgentOptions options,ProtectedState state,UpdateJournal journal)
 {
  if(journal.SignedManifest is null||options.UpdatePublicKeyFile is null||!options.AllowAgentUpdates||options.ExternalEngineExecutable is not null)throw new InvalidOperationException("Unix update authorization missing");
  UnixPrivatePaths.File(options.UpdatePublicKeyFile);using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(options.UpdatePublicKeyFile));
  var signed=UpdateProtocol.Verify(journal.SignedManifest,key,DateTimeOffset.UtcNow);
  if(signed!=journal.Manifest||signed.Platform!=options.Platform||signed.Version==options.Version||Version.Parse(signed.Version)<=Version.Parse(options.Version))throw new CryptographicException("Unix update signature, version or platform rejected");
  _=state;
 }
 private static Task Stop(CancellationToken ct)=>OperatingSystem.IsMacOS()?Run("/bin/launchctl",["bootout","system/"+UnixServices.MacLabel],ct):Run("/usr/bin/systemctl",["stop",UnixServices.ServiceName+".service"],ct);
 private static Task Start(CancellationToken ct)=>OperatingSystem.IsMacOS()?Run("/bin/launchctl",["bootstrap","system","/Library/LaunchDaemons/"+UnixServices.MacLabel+".plist"],ct):Run("/usr/bin/systemctl",["start",UnixServices.ServiceName+".service"],ct);
 private static async Task WaitForEngine(AgentOptions options,ProtectedState state,CancellationToken ct)
 {
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(90));using var adapter=new DuplicatiAdapter(options,state);
  for(var i=0;i<180;i++){try{await adapter.ReadJobs(timeout.Token);return;}catch(Exception error)when(error is HttpRequestException or InvalidOperationException){await Task.Delay(500,timeout.Token);}}
  throw new InvalidOperationException("Updated engine failed startup");
 }
 private static async Task Run(string executable,string[] args,CancellationToken ct)
 {
  var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var argument in args)start.ArgumentList.Add(argument);
  using var process=Process.Start(start)??throw new InvalidOperationException("Service supervisor unavailable");var output=process.StandardOutput.ReadToEndAsync(ct);var errors=process.StandardError.ReadToEndAsync(ct);
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(60));
  try{await process.WaitForExitAsync(timeout.Token);await output;await errors;if(process.ExitCode!=0)throw new InvalidOperationException("Service supervisor rejected operation");}
  finally{if(!process.HasExited)process.Kill(true);}
 }
}
public sealed record EnginePauseState(bool Paused,DateTimeOffset? Until);
public sealed partial class DuplicatiAdapter
{
 public async Task<bool> IsPaused(CancellationToken ct)
 {using var response=await client.GetAsync("/api/v1/serverstate",ct);response.EnsureSuccessStatusCode();using var state=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));return Get(state.RootElement,"ProgramState").ToString()=="Paused";}
 public async Task<EnginePauseState> PauseState(CancellationToken ct)
 {
  using var response=await client.GetAsync("/api/v1/serverstate",ct);response.EnsureSuccessStatusCode();using var status=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  var end=Date(Get(status.RootElement,"EstimatedPauseEnd"));return new(Get(status.RootElement,"ProgramState").ToString()=="Paused",end?.Year>=2000?end:null);
 }
 public async Task SetPause(bool paused,CancellationToken ct,DateTimeOffset? until=null,bool indefinitely=false)
 {
  var duration=until is {} end?Math.Max(1,(int)(end-DateTimeOffset.UtcNow).TotalSeconds)+"s":indefinitely?"":"5m";
  using var response=await client.PostAsync(paused?"/api/v1/serverstate/pause?pauseTransfers=false"+(duration==""?"":"&duration="+duration):"/api/v1/serverstate/resume",null,ct);response.EnsureSuccessStatusCode();
 }

}
