using System.Security.Cryptography;
using System.Text.Json;
using DariaTech.Console.Security;
using DariaTech.Console.Api;
using DariaTech.Agent;
using DariaTech.Contracts;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
namespace DariaTech.Tests;

public sealed class SecurityTests
{
 [Test]public void ManagedEngineKeepsSecretsOffCommandLineAndDisablesVendorReporting()
 {
  var start=ManagedEngine.CreateStartInfo("C:\\agent\\engine\\Duplicati.Server.exe","C:\\state\\engine","http://127.0.0.1:8210","test-local-password","test-db-key");
  Assert.That(string.Join(" ",start.ArgumentList),Does.Not.Contain("test-local-password").And.Not.Contain("test-db-key"));
  Assert.That(start.Environment["DUPLICATI__WEBSERVICE_PASSWORD"],Is.EqualTo("test-local-password"));
  Assert.That(start.Environment["SETTINGS_ENCRYPTION_KEY"],Is.EqualTo("test-db-key"));
  Assert.That(start.Environment["DO_NOT_TRACK"],Is.EqualTo("1"));
  Assert.That(start.ArgumentList,Does.Contain("--require-db-encryption-key=true"));
  Assert.Throws<InvalidOperationException>(()=>ManagedEngine.CreateStartInfo("engine.exe","data","http://example.com:8210","password","key"));
 }
 [Test]public void AgentReleaseManifestAcceptsOnlyThisRepositoryAndPlatformsAndKeepsTokenOutOfArguments()
 {
  const string repo="DariaTech-de/DariaTech-Backup";const string tag="agent-v0.2.0";var prefix=$"https://github.com/{repo}/releases/download/{tag}/";var sha=new string('b',64);
  string Manifest(params object[] assets)=>JsonSerializer.Serialize(new{version="0.2.0",tag,created=DateTimeOffset.UtcNow,assets});
  Assert.That(DestinationCatalog.All.SelectMany(x=>x.Options).Select(x=>x.Name),Has.None.EqualTo("s3-ext-usehttp").And.None.EqualTo("s3-ext-serviceurl"),"options that bypass use-ssl are not offered");
  Assert.That(DestinationCatalog.TransportSecure("b2",new Dictionary<string,string>{{"b2-download-url","http://files.example"}}),Is.False);
  Assert.That(DestinationCatalog.TransportSecure("b2",new Dictionary<string,string>{{"b2-download-url","https://files.example"}}),Is.True);
  Assert.That(DestinationCatalog.TransportSecure("aliyunoss",new Dictionary<string,string>{{"oss-endpoint","oss-cn-hangzhou.aliyuncs.com"}}),Is.False,"the Aliyun SDK defaults to HTTP without a scheme");
  Assert.That(DestinationCatalog.TransportSecure("s3",new Dictionary<string,string>{{"use-ssl","true"},{"s3-server-name","http://minio.local"}}),Is.False);
  var parsed=DariaTech.Console.Services.AgentReleases.Parse(Manifest(
   new{platform="linux-x64",label="Linux",file="a.tar.gz",sha256=sha,size=10,url=prefix+"a.tar.gz"},
   new{platform="linux-x64",label="Dup",file="b.tar.gz",sha256=sha,size=10,url=prefix+"b.tar.gz"},
   new{platform="osx-arm64",label="Mac",file="m.tar.gz",sha256=sha,size=10,url="https://evil.example/m.tar.gz"},
   new{platform="osx-x64",label="Mac",file="m.tar.gz",sha256="xyz",size=10,url=prefix+"m.tar.gz"},
   new{platform="win-x64",label="Win",file="w.exe",sha256=sha,size=10,url=prefix+"sub/w.exe"},
   new{platform="freebsd-x64",label="BSD",file="f.tar.gz",sha256=sha,size=10,url=prefix+"f.tar.gz"}),repo,tag,true,"page")!;
  Assert.That(parsed.Assets.Select(x=>x.Platform),Is.EqualTo(new[]{"linux-x64"}));Assert.That(parsed.Assets[0].File,Is.EqualTo("a.tar.gz"));
  Assert.That(DariaTech.Console.Services.AgentReleases.Parse(Manifest(new{platform="linux-x64",label="L",file="a",sha256=sha,size=10,url=prefix+"a"}),repo,"agent-v0.3.0",false,"p"),Is.Null,"tag must match the release");
  Assert.That(DariaTech.Console.Services.AgentReleases.Parse(Manifest(new{platform="linux-x64",label="L",file="a",sha256=sha,size=10,url=prefix+"a"}),"Other/Repo",tag,false,"p"),Is.Null);
  var token=new string('C',64);var asset=parsed.Assets[0];
  foreach(var platform in DariaTech.Console.Services.AgentReleases.Platforms)
  {
   var command=DariaTech.Console.Services.AgentReleases.Command(platform,asset,"https://backup.example",token,true);
   var local=DariaTech.Console.Services.AgentReleases.Command(platform,asset,"https://backup.example",token,false);
   const string key="-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEexample\n-----END PUBLIC KEY-----";
   var remote=DariaTech.Console.Services.AgentReleases.Command(platform,asset,"https://backup.example",token,true,key);
   Assert.That(remote,Does.Contain(key).And.Contain(platform=="win-x64"?"/commandkeyfile=$k":"--command-key-file"));
   Assert.That(command,Does.Not.Contain("BEGIN PUBLIC KEY").And.Not.Contain("allowremote").And.Not.Contain("allow-remote-commands"));
   Assert.That(command,Does.Contain(asset.Url).And.Contain("https://backup.example"));
   Assert.That(command,Does.Contain(platform=="win-x64"?"/allowmanaged=1":"--allow-managed-configuration"));Assert.That(local,Does.Not.Contain("allowmanaged").And.Not.Contain("allow-managed"));
   Assert.That(command.Split('\n').Count(x=>x.Contains(token)),Is.EqualTo(1),"the token is written once into a protected file");
   if(platform!="win-x64")Assert.That(command.Split('\n')[^2],Does.StartWith("sudo \"$d/install.sh\""),"the installer runs last so its status is the command's status");
   // Every step must stop the whole command on failure and the installer's status must be its result.
   if(platform=="win-x64")Assert.That(command,Does.Contain("Get-FileHash").And.Contain("/tokenfile=$t").And.Contain(sha.ToUpperInvariant()).And.Contain("$ErrorActionPreference='Stop'").And.Contain("$p.ExitCode -ne 0").And.StartWith("# PowerShell").And.EndWith("}"));
   else Assert.That(command,Does.Contain("set -eu").And.EndWith(")").And.Contain("--enrollment-token-file").And.Contain("install -m 600").And.Contain(sha+"  ").And.Contain(platform.StartsWith("osx")?"shasum -a 256":"sha256sum"));
  }
 }
 [Test]public void ConsoleCreatesItsCommandKeyOnceAndStoresItEncrypted()
 {
  var dir=Path.Combine(Path.GetTempPath(),"dariatech-keys-"+Guid.NewGuid());
  try
  {
   var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Security:KeyDirectory",dir}}).Build();
   var store=new MarkingSecretStore();
   var first=new DariaTech.Console.Security.CommandSigning(config,store);var publicKey=first.PublicKeyPem;
   Assert.That(publicKey,Does.StartWith("-----BEGIN PUBLIC KEY-----"));
   var stored=File.ReadAllText(Path.Combine(dir,"command-signing.key"));Assert.That(stored,Does.StartWith("protected:").And.Not.Contain("BEGIN PRIVATE KEY"));
   Assert.That(new DariaTech.Console.Security.CommandSigning(config,store).PublicKeyPem,Is.EqualTo(publicKey),"a restart keeps the pinned key");
   var now=DateTimeOffset.UtcNow;var command=new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"1",RemoteAction.VerifyBackup,null,now,now.AddMinutes(5));
   using var verifier=ECDsa.Create();verifier.ImportFromPem(publicKey);
   Assert.That(CommandProtocol.Verify(first.Sign(command),verifier,command.DeviceId,now).Id,Is.EqualTo(command.Id));
  }
  finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
 }
 private sealed class MarkingSecretStore:DariaTech.Console.Security.ISecretStore
 {
  public string Protect(Guid tenant,string purpose,string value)=>"protected:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
  public string Unprotect(Guid tenant,string purpose,string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value["protected:".Length..]));
 }
 [Test]public void PastedWebDavAddressesOfNextcloudAndOpenCloudBecomeValidDestinations()
 {
  var (host,port,path)=DariaTech.Console.Services.DestinationForm.Split("https://ocloud.dariatech.de/dav","","/spaces/2d52bc17-d0bf$0d08febf-0381");
  Assert.That((host,port,path),Is.EqualTo(("ocloud.dariatech.de","","dav/spaces/2d52bc17-d0bf$0d08febf-0381")));
  var url=DariaTech.Console.Services.DestinationForm.Build("webdav",host,port,path);
  Assert.That(url,Is.EqualTo("webdav://ocloud.dariatech.de/dav/spaces/2d52bc17-d0bf$0d08febf-0381"));
  Assert.That(ConfigurationPolicy.ValidDestination(url,new(){{"use-ssl","true"},{"auth-username","admin"},{"auth-password","app-token"}}),Is.True);
  // The whole WebDAV URL pasted into the server field, folder field empty or repeating it.
  Assert.That(DariaTech.Console.Services.DestinationForm.Split("https://ocloud.dariatech.de/dav/spaces/abc$def","",""),Is.EqualTo(("ocloud.dariatech.de","","dav/spaces/abc$def")));
  Assert.That(DariaTech.Console.Services.DestinationForm.Split("https://cloud.firma.de:8443/remote.php/dav/files/admin","","remote.php/dav/files/admin/Backups"),Is.EqualTo(("cloud.firma.de","8443","remote.php/dav/files/admin/Backups")));
  Assert.That(DariaTech.Console.Services.DestinationForm.Split("cloud.firma.de/remote.php/dav/files/admin","","Backups"),Is.EqualTo(("cloud.firma.de","","remote.php/dav/files/admin/Backups")));
  Assert.That(DariaTech.Console.Services.DestinationForm.Split("cloud.firma.de","443","Backups"),Is.EqualTo(("cloud.firma.de","443","Backups")));
  Assert.That(DariaTech.Console.Services.DestinationForm.Parse(url).Path,Is.EqualTo("dav/spaces/2d52bc17-d0bf$0d08febf-0381"));
 }
 [Test]public void TotpMatchesRfc6238AndRejectsReplay()
 {
  const string secret="GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
  Assert.That(Totp.Code(secret,1),Is.EqualTo("287082"));
  Assert.That(Totp.Validate(secret,"287082",DateTimeOffset.FromUnixTimeSeconds(59),-1),Is.EqualTo(1));
  Assert.That(Totp.Validate(secret,"287082",DateTimeOffset.FromUnixTimeSeconds(59),1),Is.Null);
  Assert.That(Totp.Validate(secret,"123456",DateTimeOffset.FromUnixTimeSeconds(59),-1),Is.Null);
 }
 [Test]public void EncryptedSecretsAreTenantAndPurposeBoundAndDetectTampering()
 {
  var file=Path.GetTempFileName();File.WriteAllText(file,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
  try
  {
   var store=new EncryptedSecretStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Security:MasterKeyFile",file}}).Build());
   var tenant=Guid.NewGuid();var encrypted=store.Protect(tenant,"backup","test-secret");Assert.That(encrypted,Does.Not.Contain("test-secret"));
   Assert.That(store.Unprotect(tenant,"backup",encrypted),Is.EqualTo("test-secret"));
   Assert.Throws<AuthenticationTagMismatchException>(()=>store.Unprotect(Guid.NewGuid(),"backup",encrypted));
   Assert.Throws<AuthenticationTagMismatchException>(()=>store.Unprotect(tenant,"storage",encrypted));
   var parts=encrypted.Split(':');var bytes=Convert.FromBase64String(parts[2]);bytes[^1]^=1;Assert.Throws<AuthenticationTagMismatchException>(()=>store.Unprotect(tenant,"backup",string.Join(':',parts[0],parts[1],Convert.ToBase64String(bytes))));
  }finally{File.Delete(file);}
 }
 [Test]public void TelemetryRejectsRawErrorsAndInvalidCounts()
 {
  var run=new RunReport("1",DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow,RunStatus.Failed,10,1,12,null,"BackupFailed");
  var request=new HeartbeatRequest("0.1.0","Windows",true,[new("1","Backup",null,run)]);
  Assert.That(AgentApi.Valid(request),Is.True);
  Assert.That(AgentApi.Valid(request with{Jobs=[new("1","Backup",null,run with{ErrorCode="s3://secret:password@example.com"})]}),Is.False);
  Assert.That(AgentApi.Valid(request with{Jobs=[new("1","Backup",null,run with{Bytes=-1})]}),Is.False);
  Assert.That(AgentApi.Valid(request with{Jobs=[new("1","Backup",null,run with{Progress=double.NaN})]}),Is.False);
  Assert.That(AgentApi.Valid(request with{Jobs=[new("1","Backup",null,run with{Completed=null})]}),Is.False);
  var incident=run with{Completed=null,Bytes=null,Files=null,StorageBytes=null,ErrorCode="EngineOperationFailed"};
  Assert.That(AgentApi.Valid(request with{Jobs=[new("1","Backup",null,incident)]}),Is.True);
  Assert.That(AgentApi.Valid(request with{Jobs=[new("1","Backup",null,incident with{Bytes=0})]}),Is.False);
 }
 [Test]public void EngineResultAdapterOnlyExportsAllowlistedStatistics()
 {
  using var result=JsonDocument.Parse("""{"MainOperation":"Backup","BeginTime":"2026-10-01T12:00:00Z","EndTime":"2026-10-01T12:10:00Z","ParsedResult":"Warning","ExaminedFiles":12,"SizeOfExaminedFiles":1024,"Warnings":["password=DoNotSend"],"Errors":["patient-name"]}""");
  using var meta=JsonDocument.Parse("""{"TargetFilesSize":"4096","LastErrorMessage":"secret"}""");
  var report=DuplicatiAdapter.ParseResult(result.RootElement,meta.RootElement);Assert.That(report!.Status,Is.EqualTo(RunStatus.Warning));
  var json=JsonSerializer.Serialize(report);Assert.That(json,Does.Not.Contain("DoNotSend").And.Not.Contain("patient-name").And.Not.Contain("LastErrorMessage"));
  Assert.That(report.StorageBytes,Is.EqualTo(4096));Assert.That(report.RetentionError,Is.Null);
  using var unhealthy=JsonDocument.Parse("""{"MainOperation":"Backup","BeginTime":"2026-10-01T12:00:00Z","EndTime":"2026-10-01T12:10:00Z","ParsedResult":"Error","BackendStatistics":{"FreeQuotaSpace":1,"TotalQuotaSpace":100,"ReportedQuotaError":true},"DeleteResults":{"ParsedResult":"Error"}}""");
  var signal=DuplicatiAdapter.ParseResult(unhealthy.RootElement,default)!;Assert.That(signal.QuotaFreeBytes,Is.EqualTo(1));Assert.That(signal.QuotaError,Is.True);Assert.That(signal.RetentionError,Is.True);
 }
 [Test]public void ManagedConfigurationRejectsScriptsUnencryptedStorageAndHostKeyBypass()
 {
  var d=new ManagedBackupDefinition("Daily",["C:\\Data"],"file:///D:/Backup/","test-only-long-passphrase",new(),30,[],null);
  Assert.That(ConfigurationPolicy.Valid(d),Is.True);
  Assert.That(ConfigurationPolicy.Valid(d with{BackendOptions=new(){{"run-script-before","evil.exe"}}}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="s3://bucket/path"}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="s3://bucket/path",BackendOptions=new(){{"use-ssl","true"}}}),Is.True);
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="ssh://storage.example/path",BackendOptions=new(){{"ssh-accept-any-fingerprints","true"}}}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(d with{Sources=["relative/path"]}),Is.False);
 }
 [Test]public void DeviceQueriesAreBoundToNoJobAndValidateTheirPayload()
 {
  var now=DateTimeOffset.UtcNow;var device=Guid.NewGuid();var tenant=Guid.NewGuid();
  DeviceCommand Make(RemoteAction a,string job,CatalogRequest? c=null,DestinationTest? t=null)=>new(Guid.NewGuid(),tenant,device,job,a,null,now,now.AddMinutes(5),c,Test:t);
  Assert.That(CommandProtocol.Valid(Make(RemoteAction.BrowseFolders,"0",new(null,null)),device,now),Is.True,"drive list");
  Assert.That(CommandProtocol.Valid(Make(RemoteAction.BrowseFolders,"0",new(null,"C:\\Users")),device,now),Is.True);
  Assert.That(CommandProtocol.Valid(Make(RemoteAction.BrowseFolders,"1",new(null,"C:\\Users")),device,now),Is.False,"device queries carry no job");
  Assert.That(CommandProtocol.Valid(Make(RemoteAction.BrowseFolders,"0",new(now,"C:\\Users")),device,now),Is.False,"no snapshot");
  var sftp=new DestinationTest("ssh://host/path",new(){{"ssh-fingerprint","ssh-ed25519 256 aa"}},false);
  Assert.That(CommandProtocol.Valid(Make(RemoteAction.TestDestination,"0",null,sftp),device,now),Is.True);
  Assert.That(CommandProtocol.Valid(Make(RemoteAction.TestDestination,"0",null,new("ftp://host/path",new(),false)),device,now),Is.False,"plain FTP");
  Assert.That(CommandProtocol.Valid(Make(RemoteAction.RunBackup,"1",null,sftp),device,now),Is.False,"job commands carry no destination");
  Assert.That(CommandProtocol.Valid(Make(RemoteAction.RunBackup,"0"),device,now),Is.False);
 }
 [Test]public void ManagedConfigurationAcceptsEngineDestinationsWithTheirOwnOptionsOnly()
 {
  var d=new ManagedBackupDefinition("Daily",["C:\\Data"],"file:///D:/Backup/","test-only-long-passphrase",new(),30,[],null);
  Assert.That(DestinationCatalog.All.Count,Is.GreaterThan(20));
  Assert.That(DestinationCatalog.Find("rclone"),Is.Null,"rclone options name local executables");
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="b2://bucket/folder",BackendOptions=new(){{"b2-accountid","id"},{"b2-applicationkey","key"}}}),Is.True);
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="b2://bucket/folder",BackendOptions=new(){{"aws-access-key-id","id"}}}),Is.False,"options of another backend");
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="smb://nas/share/backup",BackendOptions=new(){{"auth-username","u"},{"auth-password","p"},{"transport","directtcp"}}}),Is.True);
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="smb://nas/share/backup",BackendOptions=new(){{"transport","carrier-pigeon"}}}),Is.False,"enumeration value");
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="ftp://host/backup",BackendOptions=new(){{"auth-username","u"}}}),Is.False,"plain FTP");
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="ftp://host/backup",BackendOptions=new(){{"ftp-encryption-mode","Explicit"}}}),Is.True);
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="webdavs://dav.example/backup"}),Is.True,"TLS scheme suffix as in the engine");
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="webdav://dav.example/backup",BackendOptions=new(){{"auth-username","line\nbreak"},{"use-ssl","true"}}}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="webdav://dav.example/backup",BackendOptions=new(){{"use-ssl","yes"}}}),Is.False,"boolean value");
  Assert.That(ConfigurationPolicy.Valid(d with{TargetUrl="ssh://storage.example/path",BackendOptions=new(){{"ssh-fingerprint","ssh-ed25519 256 aa:bb"},{"ssh-keyfile","C:\\key"}}}),Is.False);
 }
 [Test]public void CommandsRejectTamperingWrongDevicesExpiryAndRestoreTraversal()
 {
  using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var device=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
  var command=new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),device,"1",RemoteAction.RunBackup,null,now,now.AddMinutes(5));
  var signed=CommandProtocol.Sign(command,key);Assert.That(CommandProtocol.Verify(signed,key,device,now),Is.EqualTo(command));
  Assert.Throws<CryptographicException>(()=>CommandProtocol.Verify(signed,key,Guid.NewGuid(),now));
  Assert.Throws<CryptographicException>(()=>CommandProtocol.Verify(signed,key,device,now.AddMinutes(6)));
  var bytes=Convert.FromBase64String(signed.Payload);bytes[^2]^=1;Assert.Throws<CryptographicException>(()=>CommandProtocol.Verify(signed with{Payload=Convert.ToBase64String(bytes)},key,device,now));
  Assert.That(CommandProtocol.Valid(command with{Action=RemoteAction.Restore,Restore=new(now,["C:\\file"],"../../Windows")},device,now),Is.False);
  Assert.That(CommandProtocol.Valid(command with{LocalJobId="1/run-script"},device,now),Is.False);
  Assert.Throws<InvalidOperationException>(()=>DuplicatiAdapter.RestoreDestination(null,"restore-1"));
 }
 [Test]public async Task UpdatesRequireIndependentSignatureHashesExpiryAndMonotonicVersions()
 {
  using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);var now=DateTimeOffset.UtcNow;
  var bytes=RandomNumberGenerator.GetBytes(2048);var m=new AgentUpdateManifest(Guid.NewGuid(),12,"DariaTechBackupAgent","win-x64","0.3.0.0","https://github.com/DariaTech-de/DariaTech-Backup/releases/download/test/Setup.exe",Convert.ToHexString(SHA256.HashData(bytes)),bytes.Length,now,now.AddDays(7));
  var signed=UpdateProtocol.Sign(m,key);Assert.That(UpdateProtocol.Verify(signed,key,now),Is.EqualTo(m));
  Assert.That(UpdateProtocol.Valid(m with{Platform="linux-arm64"},now),Is.True);
  Assert.That(UpdateProtocol.Valid(m with{Platform="linux-arm"},now),Is.False);
  Assert.That(UpdateProtocol.Newer(m,"0.2.0.0",11,"linux-x64"),Is.False);
  Assert.Throws<CryptographicException>(()=>UpdateProtocol.Verify(signed,other,now));
  Assert.Throws<CryptographicException>(()=>UpdateProtocol.Verify(signed,key,now.AddDays(8)));
  Assert.That(UpdateProtocol.Newer(m,"0.2.0.0",11),Is.True);Assert.That(UpdateProtocol.Newer(m,"0.2.0.0",12),Is.False);Assert.That(UpdateProtocol.Newer(m,"0.4.0.0",11),Is.False);
  Assert.That(AgentUpdates.AllowedDownload(new Uri("http://github.com/setup"),["github.com"]),Is.False);
  Assert.That(AgentUpdates.AllowedDownload(new Uri("https://github.com.attacker.invalid/setup"),["github.com"]),Is.False);
  Assert.That(AgentUpdates.AllowedDownload(new Uri("https://127.0.0.1/setup"),["127.0.0.1"]),Is.False);
  var file=Path.GetTempFileName();try{await File.WriteAllBytesAsync(file,bytes);await AgentUpdates.VerifyArtifact(file,m,CancellationToken.None);bytes[0]^=1;await File.WriteAllBytesAsync(file,bytes);Assert.ThrowsAsync<CryptographicException>(async()=>await AgentUpdates.VerifyArtifact(file,m,CancellationToken.None));}finally{File.Delete(file);}
 }
 [Test]public void KeyRotationReadsLegacyEnvelopesAndWritesOnlyWithTheNewKey()
 {
  var directory=Path.Combine(Path.GetTempPath(),"keyring-"+Guid.NewGuid());Directory.CreateDirectory(directory);
  var old=Path.Combine(directory,"old.key");var current=Path.Combine(directory,"new.key");File.WriteAllText(old,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));File.WriteAllText(current,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
  try
  {
   using var oldStore=new EncryptedSecretStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Security:MasterKeyFile",old}}).Build());
   var tenant=Guid.NewGuid();var previous=oldStore.Protect(tenant,"rotation-test","test-only-old-secret");var legacy=previous.Split(':')[2];
   using var rotated=new EncryptedSecretStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Security:MasterKeyFile",current},{"Security:LegacyMasterKeyFiles",old}}).Build());
   Assert.That(rotated.Unprotect(tenant,"rotation-test",previous),Is.EqualTo("test-only-old-secret"));Assert.That(rotated.Unprotect(tenant,"rotation-test",legacy),Is.EqualTo("test-only-old-secret"));
   var next=rotated.Protect(tenant,"rotation-test","test-only-new-secret");Assert.That(next.Split(':')[1],Is.Not.EqualTo(previous.Split(':')[1]));
   Assert.Throws<CryptographicException>(()=>oldStore.Unprotect(tenant,"rotation-test",next));Assert.Throws<AuthenticationTagMismatchException>(()=>rotated.Unprotect(Guid.NewGuid(),"rotation-test",legacy));
  }finally{Directory.Delete(directory,true);}
 }
 [TestCase(null,null,"Completed")]
 [TestCase("test-secret-in-engine-error",null,"Failed")]
 [TestCase(null,"test-secret-in-engine-exception","Failed")]
 public async Task TaskCompletionDoesNotHideResultErrorsOrForwardSecretText(string? error,string? exception,string expected)
 {
  var directory=Path.Combine(Path.GetTempPath(),"dt-task-result-"+Guid.NewGuid());
  try
  {
   var options=new AgentOptions{StateDirectory=directory};var state=new ProtectedState(options);
   using var adapter=new DuplicatiAdapter(options,state,new TaskResultHandler(JsonSerializer.Serialize(new{Status="Completed",ErrorMessage=error,Exception=exception})));
   var receipt=await adapter.TaskReceipt(1,CancellationToken.None);Assert.That(receipt.Status,Is.EqualTo(expected));
   Assert.That(JsonSerializer.Serialize(receipt),Does.Not.Contain("test-secret"));Assert.That(receipt.ErrorCode,Is.EqualTo(expected=="Failed"?"EngineTaskFailed":null));
  }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
 }
 private sealed class TaskResultHandler(string result):HttpMessageHandler
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(result)});
 }

 [Test]public void ExternalEngineCannotEnableBundledAutomaticUpdatesOrUnmanagedExecution()
 {
  Assert.Throws<InvalidOperationException>(()=>new AgentOptions{ExternalEngineExecutable="C:/Program Files/Duplicati/Duplicati.Server.exe"}.Validate());
  Assert.Throws<InvalidOperationException>(()=>new AgentOptions{ManageEngine=true,AllowAgentUpdates=true,ExternalEngineExecutable="C:/Program Files/Duplicati/Duplicati.Server.exe"}.Validate());
  if(!OperatingSystem.IsWindows())
   Assert.Throws<InvalidOperationException>(()=>EngineInstallation.ValidateExternal("/tmp/Duplicati.Server.exe"));
 }

 [Test]public void AgentRejectsRemoteEngineAndInsecureConsole()
 {
  Assert.Throws<InvalidOperationException>(()=>new AgentOptions{EngineUrl="https://attacker.example"}.Validate());
  Assert.Throws<InvalidOperationException>(()=>new AgentOptions{ConsoleUrl="http://backup.dariatech.de"}.Validate());
 }
}
