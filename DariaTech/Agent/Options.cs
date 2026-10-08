namespace DariaTech.Agent;
public sealed class AgentOptions
{
 public string ConsoleUrl {get;set;}="https://backup.dariatech.de";
 public string EngineUrl {get;set;}=OperatingSystem.IsWindows()?"http://127.0.0.1:8200":"https://127.0.0.1:8210";
 public string StateDirectory {get;set;}=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"DariaTechBackup"):OperatingSystem.IsMacOS()?"/Library/Application Support/DariaTechBackup/state":"/var/lib/dariatech-backup";
 public string? ConfigurationFile {get;set;}
 public string? LinuxKeyFile {get;set;}
 public int HeartbeatSeconds {get;set;}=60;
 public bool AllowAgentUpdates {get;set;}
 public string? UpdatePublicKeyFile {get;set;}
 public string[] UpdateDownloadHosts {get;set;}=["github.com","release-assets.githubusercontent.com"];
 public bool AllowRemoteCommands {get;set;}
 public string? CommandPublicKeyFile {get;set;}
 public string? RestoreRoot {get;set;}
 public bool AllowManagedConfiguration {get;set;}
 public bool ManageEngine {get;set;}
 public bool AllowSaasWorkloads {get;set;}
 public bool AllowSaasRestore {get;set;}
 public string[] AllowedSaasTenants {get;set;}=[];
 public bool AllowUnlicensedSaasDevelopment {get;set;}
 public string? ExternalEngineExecutable {get;set;}
 public string Platform=>(OperatingSystem.IsWindows()?"win":OperatingSystem.IsMacOS()?"osx":OperatingSystem.IsLinux()?"linux":"unknown")+"-"+(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture==System.Runtime.InteropServices.Architecture.Arm64?"arm64":System.Runtime.InteropServices.RuntimeInformation.OSArchitecture==System.Runtime.InteropServices.Architecture.X64?"x64":"unsupported");
 public string Version=>typeof(AgentOptions).Assembly.GetName().Version?.ToString()??"unknown";
 public void Validate()
 {
  if(!DariaTech.Contracts.UpdateProtocol.Platforms.Contains(Platform))throw new PlatformNotSupportedException("Supported OS and x64/ARM64 architecture required");
  if(!string.IsNullOrWhiteSpace(ExternalEngineExecutable)&&(!ManageEngine||AllowAgentUpdates))throw new InvalidOperationException("External engine requires managed operation and separate manual engine/agent updates");
  if(AllowSaasWorkloads&&(!ManageEngine||AllowedSaasTenants is not {Length:>0 and <=100}||AllowedSaasTenants.Any(string.IsNullOrWhiteSpace)))throw new InvalidOperationException("SaaS requires a managed engine and local cloud-tenant allowlist");
  if(AllowSaasRestore&&(!AllowSaasWorkloads||!AllowRemoteCommands))throw new InvalidOperationException("SaaS restore requires local SaaS and signed-command opt-ins");
  if(AllowUnlicensedSaasDevelopment&&!SaasWorkloads.DevelopmentTestingAllowed)throw new InvalidOperationException("Unlicensed SaaS testing requires the isolated development environment");
  if(AllowRemoteCommands&&!ManageEngine)throw new InvalidOperationException("Remote commands require an installer-managed engine instance");
  if(AllowRemoteCommands&&(string.IsNullOrWhiteSpace(CommandPublicKeyFile)||!File.Exists(CommandPublicKeyFile)))throw new InvalidOperationException("Pinned command public key required");
  if(AllowAgentUpdates&&!OperatingSystem.IsWindows()&&(ConfigurationFile is null||!Path.IsPathFullyQualified(ConfigurationFile)))throw new InvalidOperationException("Unix update service configuration required");
  if(AllowAgentUpdates&&(!ManageEngine||string.IsNullOrWhiteSpace(UpdatePublicKeyFile)||!File.Exists(UpdatePublicKeyFile)||UpdateDownloadHosts.Length==0))throw new InvalidOperationException("Managed engine and pinned update key required");
  var remote=new Uri(ConsoleUrl);if(remote.Scheme!="https"||!string.IsNullOrEmpty(remote.UserInfo)||remote.AbsolutePath!="/"||!string.IsNullOrEmpty(remote.Query)||!string.IsNullOrEmpty(remote.Fragment))throw new InvalidOperationException("ConsoleUrl must be an HTTPS origin");
  var local=new Uri(EngineUrl);if(!local.IsLoopback||!string.IsNullOrEmpty(local.UserInfo)||local.AbsolutePath!="/"||local.Scheme is not ("http" or "https")||local.Query!=""||local.Fragment!="")throw new InvalidOperationException("EngineUrl must be a loopback origin");
  if(ManageEngine&&(local.Scheme!=(OperatingSystem.IsWindows()?"http":"https")||local.Host is not ("127.0.0.1" or "localhost")))throw new InvalidOperationException("Managed engine requires IPv4 loopback with platform-bound transport");
  if(!OperatingSystem.IsWindows()&&(string.IsNullOrWhiteSpace(LinuxKeyFile)||!Path.IsPathFullyQualified(LinuxKeyFile)||!Path.IsPathFullyQualified(StateDirectory)))throw new InvalidOperationException("Unix requires absolute protected state and encryption key paths");
  if(HeartbeatSeconds<10||HeartbeatSeconds>3600)throw new InvalidOperationException("HeartbeatSeconds must be 10..3600");
 }
}
public sealed record AgentIdentity(Guid DeviceId,string Credential);
