using System.Security.Cryptography;
using System.Text.Json;
namespace DariaTech.Agent;

public static class UnixProvisioning
{
 public static AgentOptions Initialize(string[] args)
 {
  if(OperatingSystem.IsWindows()||UnixPrivatePaths.EffectiveUserId!=0)throw new InvalidOperationException("Unix service provisioning requires root");
  var values=new Dictionary<string,string>(StringComparer.Ordinal);var flags=new HashSet<string>(StringComparer.Ordinal);
  var allowed=new HashSet<string>(["--agent-config","--console-url","--state-directory","--engine-port","--enrollment-token-file","--command-key-file","--update-key-file","--restore-root","--external-engine","--saas-tenants"],StringComparer.Ordinal);
  var switches=new HashSet<string>(["--allow-remote-commands","--allow-managed-configuration","--allow-agent-updates","--allow-saas","--allow-saas-restore"],StringComparer.Ordinal);
  for(var i=1;i<args.Length;i++)
  {
   if(switches.Contains(args[i])){if(!flags.Add(args[i]))throw new InvalidOperationException("Duplicate provisioning option");continue;}
   if(!allowed.Contains(args[i])||i+1>=args.Length||!values.TryAdd(args[i],args[++i]))throw new InvalidOperationException("Unknown or duplicated provisioning option");
  }
  if(!values.TryGetValue("--agent-config",out var config)||!Path.IsPathFullyQualified(config))throw new InvalidOperationException("Absolute --agent-config required");
  AgentOptions options;
  if(File.Exists(config))
  {
   UnixPrivatePaths.File(config);using var existing=JsonDocument.Parse(File.ReadAllText(config));
   options=existing.RootElement.GetProperty("Agent").Deserialize<AgentOptions>()??throw new InvalidOperationException("Invalid existing agent configuration");
   if(values.TryGetValue("--console-url",out var origin)&&!SameOrigin(origin,options.ConsoleUrl))throw new InvalidOperationException("Existing identity cannot change Console origin");
   if(values.TryGetValue("--state-directory",out var requested)&&Path.GetFullPath(requested)!=Path.GetFullPath(options.StateDirectory))throw new InvalidOperationException("Existing encrypted state cannot move during provisioning");
  }
  else
  {
   options=new AgentOptions{ManageEngine=true};
   if(values.TryGetValue("--console-url",out var origin))options.ConsoleUrl=origin;
   if(values.TryGetValue("--state-directory",out var directory))options.StateDirectory=directory;
  }
  if(!Path.IsPathFullyQualified(options.StateDirectory))throw new InvalidOperationException("Absolute state directory required");
  UnixPrivatePaths.Directory(options.StateDirectory,true);
  options.LinuxKeyFile??=Path.Combine(options.StateDirectory,"agent.key");
  if(!File.Exists(options.LinuxKeyFile))
  {
   if(Directory.EnumerateFiles(options.StateDirectory,"*.bin").Any())throw new InvalidOperationException("Encryption key missing: restore it from escrow, never replace encrypted identity");
   WritePrivate(options.LinuxKeyFile,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
  }
  UnixPrivatePaths.File(options.LinuxKeyFile);
  if(values.TryGetValue("--engine-port",out var rawPort))
  {
   if(!int.TryParse(rawPort,out var port)||port is <1024 or >65535)throw new InvalidOperationException("Engine port must be 1024..65535");
   options.EngineUrl="https://127.0.0.1:"+port;
  }
  if(values.TryGetValue("--external-engine",out var external))options.ExternalEngineExecutable=external;
  if(values.TryGetValue("--command-key-file",out var command))options.CommandPublicKeyFile=PinKey(command,options.CommandPublicKeyFile,options.StateDirectory,"commands.pub");
  if(values.TryGetValue("--update-key-file",out var update))options.UpdatePublicKeyFile=PinKey(update,options.UpdatePublicKeyFile,options.StateDirectory,"updates.pub");
  if(values.TryGetValue("--restore-root",out var restore)){UnixPrivatePaths.Directory(restore,true);options.RestoreRoot=Path.GetFullPath(restore);}
  if(values.TryGetValue("--saas-tenants",out var tenants))options.AllowedSaasTenants=tenants.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
  options.AllowRemoteCommands|=flags.Contains("--allow-remote-commands");
  options.AllowManagedConfiguration|=flags.Contains("--allow-managed-configuration");
  options.AllowAgentUpdates|=flags.Contains("--allow-agent-updates");
  options.AllowSaasWorkloads|=flags.Contains("--allow-saas");
  options.AllowSaasRestore|=flags.Contains("--allow-saas-restore");
  options.ConfigurationFile=config;options.Validate();EngineInstallation.Resolve(options);
  var state=new ProtectedState(options);
  if(state.Read<AgentIdentity>("identity.bin") is null)
  {
   if(values.TryGetValue("--enrollment-token-file",out var tokenFile))
   {
    UnixPrivatePaths.File(tokenFile);var token=File.ReadAllText(tokenFile).Trim();
    if(token.Length!=64||!token.All(Uri.IsHexDigit))throw new InvalidOperationException("Invalid enrollment token");
    WritePrivate(Path.Combine(options.StateDirectory,"enrollment-token.txt"),token);
   }
   if(!File.Exists(Path.Combine(options.StateDirectory,"enrollment-token.txt")))throw new InvalidOperationException("Protected enrollment token file required for first installation");
  }
  var parent=Path.GetDirectoryName(config)!;UnixPrivatePaths.Directory(parent,true);
  WritePrivate(config,Serialize(options));
  return options;
 }
 public static string Serialize(AgentOptions options)=>JsonSerializer.Serialize(new Dictionary<string,AgentOptions>{{"Agent",options}},new JsonSerializerOptions{WriteIndented=true,IgnoreReadOnlyProperties=true});
 private static string PinKey(string source,string? existing,string state,string leaf)
 {
  UnixPrivatePaths.TrustedFile(source);var text=File.ReadAllText(source);
  using var key=ECDsa.Create();key.ImportFromPem(text);if(key.KeySize!=256)throw new CryptographicException("Pinned public key must be P-256");
  var canonical=key.ExportSubjectPublicKeyInfoPem();
  var path=existing??Path.Combine(state,leaf);
  if(File.Exists(path))
  {
   UnixPrivatePaths.File(path);using var previous=ECDsa.Create();previous.ImportFromPem(File.ReadAllText(path));
   if(!CryptographicOperations.FixedTimeEquals(previous.ExportSubjectPublicKeyInfo(),key.ExportSubjectPublicKeyInfo()))throw new CryptographicException("Pinned trust key cannot be replaced by installation");
  }
  else WritePrivate(path,canonical);
  return path;
 }
 private static bool SameOrigin(string first,string second)=>Uri.TryCreate(first,UriKind.Absolute,out var a)&&Uri.TryCreate(second,UriKind.Absolute,out var b)&&a==b;
 public static void WritePrivate(string path,string content)
 {
  if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
  UnixPrivatePaths.Directory(Path.GetDirectoryName(Path.GetFullPath(path))!,false);if(File.Exists(path))UnixPrivatePaths.File(path);
  var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try
  {
   using(var stream=new FileStream(temporary,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
   using(var writer=new StreamWriter(stream)){writer.Write(content);writer.Flush();stream.Flush(true);}
   File.Move(temporary,path,true);
  }finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
}
