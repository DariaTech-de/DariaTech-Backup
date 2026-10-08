using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DariaTech.Agent;
using DariaTech.Contracts;
using NUnit.Framework;
namespace DariaTech.Tests;

public sealed class SaasTests
{
 internal static SaasSource Office()=>new(SaasProvider.Microsoft365,"11111111-1111-1111-1111-111111111111",
  new(){{"office365-client-id","22222222-2222-2222-2222-222222222222"},{"office365-client-secret","test-only-not-a-provider-credential"}},["Users","Sites"],["Mailbox","Calendar"]);
 [Test]public void SourcePolicyRejectsExecutableFileCredentialsAndForeignOAuthEndpoints()
 {
  var source=Office();Assert.That(SaasPolicy.Valid(source),Is.True);
  Assert.That(SaasPolicy.Valid(source with{Credentials=new(source.Credentials){{"office365-graph-base-url","https://attacker.invalid"}}}),Is.False);
  Assert.That(SaasPolicy.Valid(source with{Credentials=new(source.Credentials){{"office365-certificate-path","C:/private.key"}}}),Is.False);
  Assert.That(SaasPolicy.Valid(source with{DirectoryTenant="another-domain"}),Is.False);
  Assert.That(SaasPolicy.Valid(source with{UserTypes=["NotAProviderWorkload"]}),Is.False);
  var google=new SaasSource(SaasProvider.GoogleWorkspace,"test.example",new(){{"google-client-id","ci-client"},{"google-client-secret","ci-secret"},{"google-refresh-token","ci-refresh"},{"google-admin-email","admin@test.example"}},["Users"],["Gmail","Drive"]);
  Assert.That(SaasPolicy.Valid(google),Is.False); // OAuth identity is not domain-bound; central jobs require service accounts.
  Assert.That(SaasPolicy.Valid(google with{Credentials=new(google.Credentials){["google-admin-email"]="admin@foreign.example"}}),Is.False);
  Assert.That(SaasPolicy.Valid(google with{Credentials=new(){{"google-admin-email","admin@test.example"},{"google-service-account-json",JsonSerializer.Serialize(new{type="service_account",client_email="test@ci.iam.gserviceaccount.com",private_key="-----BEGIN PRIVATE KEY----- CI ONLY",token_uri="https://attacker.invalid/token"})}}}),Is.False);
  Assert.That(SaasPolicy.SameDirectory(source,source with{DirectoryTenant="33333333-3333-3333-3333-333333333333"}),Is.False);
 }
 [Test]public void CloudDefinitionsCannotMixLocalSourcesAndPreserveLegacySerialization()
 {
  var file=new ManagedBackupDefinition("Test",["/source"],"file:///backup","test-passphrase-1234",new(),30,[],null);
  Assert.That(JsonSerializer.Serialize(file),Does.Not.Contain("Saas"));
  Assert.That(ConfigurationPolicy.Valid(file with{Saas=Office()}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(file with{Sources=[],Saas=Office()}),Is.True);
  var command=new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"1",RemoteAction.RunBackup,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5));
  Assert.That(JsonSerializer.Serialize(command),Does.Not.Contain("SaasRestore"));
  var restore=new SaasRestoreSelection(1,DateTimeOffset.UtcNow,["/virtual/file"],"users/user/mailbox",true);
  Assert.That(CommandProtocol.Valid(command with{Action=RemoteAction.RestoreSaas,SaasRestore=restore},command.DeviceId,DateTimeOffset.UtcNow),Is.True);
  Assert.That(SaasPolicy.ValidRestore(restore with{ConfirmProviderWrites=false},DateTimeOffset.UtcNow),Is.False);
  Assert.That(SaasPolicy.ValidRestore(restore with{TargetPath="../foreign"},DateTimeOffset.UtcNow),Is.False);
  Assert.That(SaasPolicy.ValidRestore(restore with{TargetPath="users/user?token=secret"},DateTimeOffset.UtcNow),Is.False);
 }
 [Test]public async Task CloudRestoreUsesNativeProviderMetadataAndProtectedLocalBinding()
 {
  var dir=Path.Combine(Path.GetTempPath(),"dt-saas-"+Guid.NewGuid());Directory.CreateDirectory(dir);
  var key=Path.Combine(dir,"agent.key");File.WriteAllText(key,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
  if(!OperatingSystem.IsWindows())File.SetUnixFileMode(key,UnixFileMode.UserRead|UnixFileMode.UserWrite);
  try
  {
   var source=Office();var options=new AgentOptions{StateDirectory=Path.Combine(dir,"state"),LinuxKeyFile=key,ManageEngine=true,AllowSaasWorkloads=true,AllowSaasRestore=true,AllowedSaasTenants=[source.DirectoryTenant]};
   var state=new ProtectedState(options);var mount=Path.Combine(Path.GetPathRoot(dir)!,"virtual-test");
   state.Write("saas-bindings.bin",new Dictionary<string,SaasJobBinding>{{"1",new(Guid.NewGuid(),2,mount,source)}});
   using var handler=new NativeRestoreFixture();using var adapter=new DuplicatiAdapter(options,state,handler);
   var command=new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"1",RemoteAction.RestoreSaas,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5),SaasRestore:new(2,DateTimeOffset.UtcNow,[Path.Combine(mount,"users","user","mail")],"users/user/mailbox",true));
   Assert.That(await adapter.RestoreSaas(command,CancellationToken.None),Is.EqualTo(42));
   Assert.That(handler.Body,Does.Contain("@office365:///users/user/mailbox").And.Not.Contain(source.Credentials["office365-client-secret"]));
   using var body=JsonDocument.Parse(handler.Body!);Assert.That(body.RootElement.GetProperty("skip_metadata").GetBoolean(),Is.False);Assert.That(body.RootElement.GetProperty("overwrite").GetBoolean(),Is.True);
   Assert.ThrowsAsync<InvalidOperationException>(async()=>await adapter.RestoreSaas(command with{SaasRestore=command.SaasRestore! with{ConfigurationRevision=1}},CancellationToken.None));
   Assert.ThrowsAsync<InvalidOperationException>(async()=>await adapter.RestoreSaas(command with{SaasRestore=command.SaasRestore! with{Paths=[Path.Combine(dir,"foreign")]}},CancellationToken.None));
   options.AllowedSaasTenants=[];Assert.ThrowsAsync<InvalidOperationException>(async()=>await adapter.RestoreSaas(command,CancellationToken.None));
  }finally{Directory.Delete(dir,true);}
 }
 private sealed class NativeRestoreFixture:HttpMessageHandler
 {
  public string? Body {get;private set;}
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   if(request.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=JsonContent.Create(new{RestoreDestinationProviderModules=new[]{new{Key="office365"}},LocalLicenseStatus=new{IsConfigured=true,IsValid=true,Features=new Dictionary<string,string>{{"duplicati:office365:users","5"},{"duplicati:office365:sites","5"}}}})};
   Body=await request.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.OK){Content=JsonContent.Create(new{ID=42})};
  }
 }
}
