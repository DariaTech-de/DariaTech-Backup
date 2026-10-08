namespace DariaTech.Agent;
public sealed class AgentOptions
{
 public string ConsoleUrl {get;set;}="https://backup.dariatech.de";
 public string EngineUrl {get;set;}="http://127.0.0.1:8200";
 public string StateDirectory {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"DariaTechBackup");
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
 public string Version=>typeof(AgentOptions).Assembly.GetName().Version?.ToString()??"unknown";
 public void Validate()
 {
  if(AllowRemoteCommands&&!ManageEngine)throw new InvalidOperationException("Remote commands require an installer-managed engine instance");
  if(AllowRemoteCommands&&(string.IsNullOrWhiteSpace(CommandPublicKeyFile)||!File.Exists(CommandPublicKeyFile)))throw new InvalidOperationException("Pinned command public key required");
  if(AllowAgentUpdates&&(!ManageEngine||string.IsNullOrWhiteSpace(UpdatePublicKeyFile)||!File.Exists(UpdatePublicKeyFile)||UpdateDownloadHosts.Length==0))throw new InvalidOperationException("Managed engine and pinned update key required");
  var remote=new Uri(ConsoleUrl);if(remote.Scheme!="https"||!string.IsNullOrEmpty(remote.UserInfo)||remote.AbsolutePath!="/"||!string.IsNullOrEmpty(remote.Query))throw new InvalidOperationException("ConsoleUrl must be an HTTPS origin");
  var local=new Uri(EngineUrl);if(!local.IsLoopback||!string.IsNullOrEmpty(local.UserInfo)||local.AbsolutePath!="/")throw new InvalidOperationException("EngineUrl must be a loopback origin");
  if(HeartbeatSeconds<10||HeartbeatSeconds>3600)throw new InvalidOperationException("HeartbeatSeconds must be 10..3600");
 }
}
public sealed record AgentIdentity(Guid DeviceId,string Credential);
