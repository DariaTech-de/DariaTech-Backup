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
   var bytes=Convert.FromBase64String(encrypted);bytes[^1]^=1;Assert.Throws<AuthenticationTagMismatchException>(()=>store.Unprotect(tenant,"backup",Convert.ToBase64String(bytes)));
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
  Assert.That(report.StorageBytes,Is.EqualTo(4096));
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
 [Test]public void AgentRejectsRemoteEngineAndInsecureConsole()
 {
  Assert.Throws<InvalidOperationException>(()=>new AgentOptions{EngineUrl="https://attacker.example"}.Validate());
  Assert.Throws<InvalidOperationException>(()=>new AgentOptions{ConsoleUrl="http://backup.dariatech.de"}.Validate());
 }
}
