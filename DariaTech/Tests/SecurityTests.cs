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
