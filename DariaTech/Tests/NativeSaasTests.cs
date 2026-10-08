using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DariaTech.Agent;
using DariaTech.Contracts;
using NUnit.Framework;
namespace DariaTech.Tests;

[TestFixture,NonParallelizable]
public sealed class NativeSaasTests
{
 [Test,Category("SaasNative")]
 public async Task OriginalProvidersAcceptManagedJobsAndRejectInvalidLiveCredentials()
 {
  var dll=Environment.GetEnvironmentVariable("DARIATECH_ENGINE_SAAS_DLL");if(string.IsNullOrWhiteSpace(dll))Assert.Ignore("Set DARIATECH_ENGINE_SAAS_DLL to a development-only full engine build");
  var directory=Path.Combine(Path.GetTempPath(),"dt-native-saas-"+Guid.NewGuid());Directory.CreateDirectory(directory);
  var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var dbKey=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
  var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(var arg in new[]{dll!,"--server-datafolder="+Path.Combine(directory,"engine"),"--webservice-interface=loopback","--webservice-port="+port,"--webservice-api-only=true","--webservice-allowed-hostnames=localhost,127.0.0.1","--disable-update-check=true","--require-db-encryption-key=true"})start.ArgumentList.Add(arg);
  start.Environment["DUPLICATI__WEBSERVICE_PASSWORD"]=password;start.Environment["SETTINGS_ENCRYPTION_KEY"]=dbKey;
  start.Environment["DO_NOT_TRACK"]="1";start.Environment["USAGEREPORTER_Duplicati_LEVEL"]="none";start.Environment["AUTOUPDATER_Duplicati_SKIP_UPDATE"]="1";
  using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
  var oldEnvironment=Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");var oldMode=Environment.GetEnvironmentVariable("DARIATECH_SAAS_TESTS");
  try
  {
   Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT","Development");Environment.SetEnvironmentVariable("DARIATECH_SAAS_TESTS","1");
   using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));using var client=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}")};
   string? token=null;
   for(var i=0;i<150;i++)
   {
    try{using var response=await client.PostAsJsonAsync("/api/v1/auth/login",new{Password=password,RememberMe=false},timeout.Token);if(response.IsSuccessStatusCode){using var auth=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));token=DuplicatiAdapter.Get(auth.RootElement,"AccessToken").GetString();break;}}catch(HttpRequestException){}
    if(process.HasExited)Assert.Fail("Native development engine exited");await Task.Delay(100,timeout.Token);
   }
   Assert.That(token,Is.Not.Null);client.DefaultRequestHeaders.Authorization=new("Bearer",token);
   using var rsa=RSA.Create(2048);
   var account=JsonSerializer.Serialize(new{type="service_account",project_id="dariatech-development-negative-test",private_key_id="not-a-live-key",private_key=rsa.ExportPkcs8PrivateKeyPem(),client_email="test@dariatech-development-negative-test.iam.gserviceaccount.com",client_id="123456789",token_uri="https://oauth2.googleapis.com/token"});
   var google=new SaasSource(SaasProvider.GoogleWorkspace,"test.invalid",new(){{"google-admin-email","admin@test.invalid"},{"google-service-account-json",account}},["Users"],["Gmail","Drive"]);
   var office=SaasTests.Office();
   var options=new AgentOptions{EngineUrl=client.BaseAddress.ToString(),StateDirectory=Path.Combine(directory,"agent"),LinuxKeyFile=Path.Combine(directory,"agent.key"),ManageEngine=true,AllowSaasWorkloads=true,AllowedSaasTenants=[office.DirectoryTenant,google.DirectoryTenant],AllowUnlicensedSaasDevelopment=true};
   File.WriteAllText(options.LinuxKeyFile,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
   if(!OperatingSystem.IsWindows())File.SetUnixFileMode(options.LinuxKeyFile,UnixFileMode.UserRead|UnixFileMode.UserWrite);
   var state=new ProtectedState(options);state.Write("engine-credential.bin",password);
   using var adapter=new DuplicatiAdapter(options,state,new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false});await adapter.ReadJobs(timeout.Token);
   foreach(var source in new[]{office,google})
   {
    var storage=Path.Combine(directory,SaasPolicy.Key(source.Provider));Directory.CreateDirectory(storage);
    var definition=new ManagedBackupDefinition("Native "+SaasPolicy.Key(source.Provider),[],new Uri(storage+Path.DirectorySeparatorChar).AbsoluteUri,"test-only-cloud-backup-passphrase",new(),30,[],null,source);
    var assignment=new ConfigurationAssignment(Guid.NewGuid(),1,definition);
    var id=await adapter.ApplyConfiguration(assignment,timeout.Token);
    Assert.That(await adapter.ApplyConfiguration(assignment,timeout.Token),Is.EqualTo(id));
    using var details=JsonDocument.Parse(await client.GetStringAsync("/api/v1/backup/"+id,timeout.Token));
    var backup=DuplicatiAdapter.Get(details.RootElement,"Backup");
    Assert.That(DuplicatiAdapter.Get(backup,"Sources")[0].ToString(),Does.Contain("|"+SaasPolicy.Key(source.Provider)+"://").And.Not.Contain("test-only-not-a-provider"));
    var settings=DuplicatiAdapter.Get(backup,"Settings");Assert.That(settings.EnumerateArray().Any(x=>DuplicatiAdapter.Get(x,"Name").ToString()=="store-metadata-content-in-database"),Is.True);
    // Optional explicit negative integration: real provider authentication with synthetic,
    // invalid credentials, never tenant data. No fake success or entitlement substitution.
    if(Environment.GetEnvironmentVariable("DARIATECH_PROVIDER_NEGATIVE_AUTH")=="1")
    {
     var command=new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),id,RemoteAction.RunBackup,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5));
     var task=await adapter.Dispatch(command,options,timeout.Token);CommandReceipt receipt;
     do{await Task.Delay(200,timeout.Token);receipt=await adapter.TaskReceipt(task,timeout.Token);}while(receipt.Status=="Accepted");
     Assert.That(receipt.Status,Is.EqualTo("Failed"),"Invalid provider credentials must never produce a successful backup receipt");
     var reports=await adapter.ReadJobs(timeout.Token);
     Assert.That(reports.Single(x=>x.LocalId==id).LastRun?.Status,Is.EqualTo(RunStatus.Failed));
     Assert.That(JsonSerializer.Serialize(reports),Does.Not.Contain("BEGIN PRIVATE KEY").And.Not.Contain(office.Credentials["office365-client-secret"]));
    }
   }
  }
  finally
  {
   Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT",oldEnvironment);Environment.SetEnvironmentVariable("DARIATECH_SAAS_TESTS",oldMode);
   if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();await output;await errors;Directory.Delete(directory,true);
  }
 }
}
