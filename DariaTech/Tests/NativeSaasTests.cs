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
  using var process=Process.Start(start)!;var started=new TaskCompletionSource();var output=EngineStartWatch.Read(process,port,started);var errors=process.StandardError.ReadToEndAsync();
  var safeMasks=new List<string>{password,dbKey};var failed=false;
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
   // The engine still initializes its settings database after the web server is up; wait like the agent does.
   await Task.WhenAny(started.Task,Task.Delay(TimeSpan.FromSeconds(30),timeout.Token));
   Assert.That(token,Is.Not.Null);client.DefaultRequestHeaders.Authorization=new("Bearer",token);
   using var rsa=RSA.Create(2048);
   var account=JsonSerializer.Serialize(new{type="service_account",project_id="dariatech-development-negative-test",private_key_id="not-a-live-key",private_key=rsa.ExportPkcs8PrivateKeyPem(),client_email="test@dariatech-development-negative-test.iam.gserviceaccount.com",client_id="123456789",token_uri="https://oauth2.googleapis.com/token"});
   var google=new SaasSource(SaasProvider.GoogleWorkspace,"test.invalid",new(){{"google-admin-email","admin@test.invalid"},{"google-service-account-json",account}},["Users"],["Gmail","Drive"]);
   var office=SaasTests.Office();safeMasks.AddRange(office.Credentials.Values);safeMasks.Add(account);safeMasks.Add(rsa.ExportPkcs8PrivateKeyPem());
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
    using var details=JsonDocument.Parse(await client.GetStringAsync("/api/v1/backup/"+id,timeout.Token));
    var backup=DuplicatiAdapter.Get(details.RootElement,"Backup");
    Assert.That(DuplicatiAdapter.Get(backup,"Sources")[0].ToString(),Does.Contain("|"+SaasPolicy.Key(source.Provider)+"://").And.Not.Contain("test-only-not-a-provider"));
    var settings=DuplicatiAdapter.Get(backup,"Settings");Assert.That(settings.EnumerateArray().Any(x=>DuplicatiAdapter.Get(x,"Name").ToString()=="store-metadata-content-in-database"),Is.True);
    AssertUniqueSettings(settings);
    Assert.That(await adapter.ApplyConfiguration(assignment,timeout.Token),Is.EqualTo(id));
    Assert.That(await adapter.ApplyConfiguration(assignment with{Revision=2,Definition=definition with{KeepVersions=90}},timeout.Token),Is.EqualTo(id));
    // Reproduce the previous client's persisted duplicate, then repair it through
    // the normal authenticated API without deleting the job or its backup chain.
    var legacy=assignment with{JobId=Guid.NewGuid(),Definition=definition with{Name=definition.Name+" legacy duplicate"}};
    var legacySettings=settings.EnumerateArray().Select(x=>
    {
     var name=DuplicatiAdapter.Get(x,"Name").ToString();
     var value=name=="passphrase"?definition.Passphrase:source.Credentials.GetValueOrDefault(name)??DuplicatiAdapter.Get(x,"Value").ToString();
     return new{Name=name,Value=value};
    }).Append(new{Name="abort-if-source-missing",Value="true"}).ToArray();
    using var created=await client.PostAsJsonAsync("/api/v1/backups",new{Backup=new{Name=legacy.Definition.Name,TargetURL=definition.TargetUrl,
     Sources=new[]{DuplicatiAdapter.Get(backup,"Sources")[0].ToString()},Settings=legacySettings,Tags=new[]{"DariaTechManaged:"+legacy.JobId.ToString("D")},Metadata=new{}}},timeout.Token);
    created.EnsureSuccessStatusCode();using var legacyResponse=JsonDocument.Parse(await created.Content.ReadAsStringAsync(timeout.Token));
    var legacyId=DuplicatiAdapter.Get(legacyResponse.RootElement,"ID").ToString();
    Assert.That(await adapter.ApplyConfiguration(legacy,timeout.Token),Is.EqualTo(legacyId));
    Assert.That(await adapter.ApplyConfiguration(legacy,timeout.Token),Is.EqualTo(legacyId));
    using var repaired=JsonDocument.Parse(await client.GetStringAsync("/api/v1/backup/"+legacyId,timeout.Token));
    AssertUniqueSettings(DuplicatiAdapter.Get(DuplicatiAdapter.Get(repaired.RootElement,"Backup"),"Settings"));
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
  catch{failed=true;throw;}
  finally
  {
   Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT",oldEnvironment);Environment.SetEnvironmentVariable("DARIATECH_SAAS_TESTS",oldMode);
   if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();var nativeLog=(await output)+"\n"+(await errors);
   if(failed)
   {
    foreach(var mask in safeMasks.Where(x=>!string.IsNullOrEmpty(x)))nativeLog=nativeLog.Replace(mask,"[REDACTED]",StringComparison.Ordinal);
    nativeLog=System.Text.RegularExpressions.Regex.Replace(nativeLog,@"-----BEGIN [^-]+-----.*?-----END [^-]+-----","[REDACTED KEY]",System.Text.RegularExpressions.RegexOptions.Singleline);
    TestContext.Error.WriteLine(nativeLog[^Math.Min(nativeLog.Length,12000)..]);
   }
   Directory.Delete(directory,true);
  }
 }
 private static void AssertUniqueSettings(JsonElement settings)
 {
  var names=settings.EnumerateArray().Select(x=>DuplicatiAdapter.Get(x,"Name").ToString()).ToArray();
  Assert.That(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(),Is.EqualTo(names.Length),"Engine option names must be unique for POST and PUT");
  var missingSource=settings.EnumerateArray().Single(x=>DuplicatiAdapter.Get(x,"Name").ToString()=="abort-if-source-missing");
  Assert.That(DuplicatiAdapter.Get(missingSource,"Value").ToString(),Is.EqualTo("true"));
 }
}
