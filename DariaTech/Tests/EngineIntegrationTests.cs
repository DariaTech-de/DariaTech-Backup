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
public sealed class EngineIntegrationTests
{
 [Test,Category("EngineIntegration")]
 public async Task EncryptedBackupReportsRealStatsAndRestoresIdenticalFiles()
 {
  var dll=Environment.GetEnvironmentVariable("DARIATECH_ENGINE_SERVER_DLL");if(string.IsNullOrWhiteSpace(dll))Assert.Ignore("Set DARIATECH_ENGINE_SERVER_DLL to run a real engine backup/restore");
  var dir=Path.Combine(Path.GetTempPath(),"dariatech-engine-"+Guid.NewGuid());Directory.CreateDirectory(dir);
  var source=Path.Combine(dir,"source");var destination=Path.Combine(dir,"storage");var restore=Path.Combine(dir,"restore");Directory.CreateDirectory(source);Directory.CreateDirectory(destination);
  var bytes=RandomNumberGenerator.GetBytes(16384);await File.WriteAllBytesAsync(Path.Combine(source,"restore-check.bin"),bytes);
  var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
  var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));var passphrase=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
  var start=new ProcessStartInfo("dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
  foreach(var a in new[]{dll!,"--server-datafolder="+Path.Combine(dir,"engine-db"),"--webservice-interface=loopback","--webservice-port="+port,"--webservice-password="+password,"--webservice-api-only=true","--webservice-allowed-hostnames=localhost,127.0.0.1","--disable-update-check=true","--webservice-suppress-welcome-page=true"})start.ArgumentList.Add(a);
  start.Environment["DO_NOT_TRACK"]="1";start.Environment["AUTOUPDATER_Duplicati_SKIP_UPDATE"]="1";start.Environment["SETTINGS_ENCRYPTION_KEY"]=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  using var server=Process.Start(start)!;var output=server.StandardOutput.ReadToEndAsync();var error=server.StandardError.ReadToEndAsync();
  try
  {
   using var ct=new CancellationTokenSource(TimeSpan.FromMinutes(3));using var client=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(15)};
   JsonDocument? auth=null;
   for(var i=0;i<100;i++)
   {
    try{using var response=await client.PostAsJsonAsync("/api/v1/auth/login",new{Password=password,RememberMe=false},ct.Token);if(response.IsSuccessStatusCode){auth=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct.Token));break;}}catch(HttpRequestException){}
    if(server.HasExited)Assert.Fail("Engine exited during startup; inspect locally captured engine logs");await Task.Delay(100,ct.Token);
   }
   Assert.That(auth,Is.Not.Null);using(auth!){client.DefaultRequestHeaders.Authorization=new("Bearer",DuplicatiAdapter.Get(auth!.RootElement,"AccessToken").GetString());}
   var settings=new[]{new{Name="encryption-module",Value="aes"},new{Name="passphrase",Value=passphrase},new{Name="dblock-size",Value="4mb"},new{Name="disable-module",Value="console-password-input"}};
   using var created=await client.PostAsJsonAsync("/api/v1/backups",new{Backup=new{Name="Encrypted smoke backup",TargetURL=new Uri(destination+Path.DirectorySeparatorChar).AbsoluteUri,Sources=new[]{source+Path.DirectorySeparatorChar},Settings=settings}},ct.Token);Assert.That(created.IsSuccessStatusCode,Is.True,$"Create backup returned {(int)created.StatusCode}");
   using var list=JsonDocument.Parse(await client.GetStringAsync("/api/v1/backups",ct.Token));var id=DuplicatiAdapter.Get(DuplicatiAdapter.Get(list.RootElement[0],"Backup"),"ID").ToString();
   using var run=await client.PostAsync($"/api/v1/backup/{id}/run",null,ct.Token);run.EnsureSuccessStatusCode();using var task=JsonDocument.Parse(await run.Content.ReadAsStringAsync(ct.Token));var taskId=DuplicatiAdapter.Get(task.RootElement,"ID").ToString();
   await WaitTask(client,taskId,ct.Token,password,passphrase);
   var key=Path.Combine(dir,"agent.key");await File.WriteAllTextAsync(key,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),ct.Token);
   if(!OperatingSystem.IsWindows())File.SetUnixFileMode(key,UnixFileMode.UserRead|UnixFileMode.UserWrite);
   var options=new AgentOptions{EngineUrl=client.BaseAddress!.ToString(),StateDirectory=Path.Combine(dir,"agent"),LinuxKeyFile=key};var state=new ProtectedState(options);state.Write("engine-credential.bin",password);
   using var adapter=new DuplicatiAdapter(options,state);var jobs=await adapter.ReadJobs(ct.Token);Assert.That(jobs,Has.Length.EqualTo(1));Assert.That(jobs[0].LastRun?.Status,Is.EqualTo(RunStatus.Success));Assert.That(jobs[0].LastRun?.Files,Is.EqualTo(1));Assert.That(jobs[0].LastRun?.Bytes,Is.EqualTo(bytes.Length));
   Assert.That(Directory.GetFiles(destination),Is.Not.Empty);Assert.That(Directory.GetFiles(destination).All(x=>x.EndsWith(".aes")),Is.True);
   // Device queries against the real engine: folder browse returns folders only; the destination test
   // reports a missing folder, creates it on request and then succeeds.
   Directory.CreateDirectory(Path.Combine(source,"Unterordner"));
   var now=DateTimeOffset.UtcNow;var device=Guid.NewGuid();
   var browse=await adapter.BrowseFolders(new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),device,"0",RemoteAction.BrowseFolders,null,now,now.AddMinutes(5),new(null,source+Path.DirectorySeparatorChar)),ct.Token);
   Assert.That(browse.Files.Select(x=>x.Path.TrimEnd('/','\\')),Does.Contain(Path.Combine(source,"Unterordner")));Assert.That(browse.Files.All(x=>x.Directory),Is.True);
   Assert.That(browse.Files.Any(x=>x.Path.EndsWith("restore-check.bin")),Is.False,"files are never listed");
   var missing=new Uri(Path.Combine(dir,"not-yet")+Path.DirectorySeparatorChar).AbsoluteUri;
   DeviceCommand Test(bool create)=>new(Guid.NewGuid(),Guid.NewGuid(),device,"0",RemoteAction.TestDestination,null,now,now.AddMinutes(5),null,Test:new(missing,new(),create));
   Assert.That((await adapter.TestDestination(Test(false),ct.Token)).ErrorCode,Is.EqualTo("DestinationFolderMissing"));
   var createdFolder=await adapter.TestDestination(Test(true),ct.Token);Assert.That(createdFolder.Status,Is.EqualTo("Completed"),createdFolder.ErrorCode);
   Assert.That(Directory.Exists(Path.Combine(dir,"not-yet")),Is.True);
   var managedDestination=Path.Combine(dir,"managed-storage");Directory.CreateDirectory(managedDestination);
   var definition=new ManagedBackupDefinition("Managed integration",[source+Path.DirectorySeparatorChar],new Uri(managedDestination+Path.DirectorySeparatorChar).AbsoluteUri,passphrase,new(),30,[],new(DateTimeOffset.UtcNow.AddDays(1),24,[DayOfWeek.Monday]));
   var assignment=new ConfigurationAssignment(Guid.NewGuid(),1,definition);
   var managedId=await adapter.ApplyConfiguration(assignment,ct.Token);
   Assert.That(await adapter.ApplyConfiguration(assignment,ct.Token),Is.EqualTo(managedId));
   Assert.That(await adapter.ApplyConfiguration(assignment with{Revision=2,Definition=definition with{KeepVersions=90}},ct.Token),Is.EqualTo(managedId));
   var afterManaged=await adapter.ReadJobs(ct.Token);Assert.That(afterManaged.Length,Is.EqualTo(2));Assert.That(afterManaged.Single(x=>x.LocalId==id).LastRun!.Status,Is.EqualTo(RunStatus.Success));
   var command=new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),id,RemoteAction.RunBackup,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5));
   using var signing=ECDsa.Create(ECCurve.NamedCurves.nistP256);var publicKey=Path.Combine(dir,"commands.pub");File.WriteAllText(publicKey,signing.ExportSubjectPublicKeyInfoPem());
   state.Write("engine-instance.bin",Guid.NewGuid());
   options.AllowRemoteCommands=true;options.CommandPublicKeyFile=publicKey;
   using var fixture=new CommandFixtureHandler(CommandProtocol.Sign(command,signing));using var console=new HttpClient(fixture){BaseAddress=new Uri("https://test-only.invalid")};
   await RemoteCommands.Synchronize(console,adapter,options,state,command.DeviceId,ct.Token);
   var remoteTask=fixture.Receipts.Single().TaskId!.Value;await WaitTask(client,remoteTask.ToString(),ct.Token,password,passphrase);
   var firstRun=(await adapter.ReadJobs(ct.Token)).Single(x=>x.LocalId==id).LastRun!.LocalRunId;
   await RemoteCommands.Synchronize(console,adapter,options,state,command.DeviceId,ct.Token);
   Assert.That(fixture.Receipts.Last().Status,Is.EqualTo("Completed"));
   Assert.That((await adapter.ReadJobs(ct.Token)).Single(x=>x.LocalId==id).LastRun!.LocalRunId,Is.EqualTo(firstRun));
   var journal=state.Read<Dictionary<Guid,CommandJournalEntry>>("commands.bin")!;
   journal[command.Id]=journal[command.Id] with{Receipt=new("Accepted",remoteTask,null)};state.Write("commands.bin",journal);state.Write("engine-instance.bin",Guid.NewGuid());
   await RemoteCommands.Synchronize(console,adapter,options,state,command.DeviceId,ct.Token);Assert.That(fixture.Receipts.Last().Status,Is.EqualTo("Indeterminate"));
   using var historyReceiver=new HistoryFixtureHandler();using var historyConsole=new HttpClient(historyReceiver){BaseAddress=new Uri("https://test-only.invalid")};
   await adapter.CaptureHistory(historyConsole,await adapter.ReadJobs(ct.Token),ct.Token);Assert.That(historyReceiver.Runs.Count(x=>x.LocalJobId==id),Is.GreaterThanOrEqualTo(2));
   var captured=historyReceiver.Runs.Count;await adapter.CaptureHistory(historyConsole,await adapter.ReadJobs(ct.Token),ct.Token);Assert.That(historyReceiver.Runs.Count,Is.EqualTo(captured));
   var points=await adapter.ReadCatalog(command with{Action=RemoteAction.ListRestorePoints},ct.Token);Assert.That(points.Points,Is.Not.Empty);
   var catalog=await adapter.ReadCatalog(command with{Action=RemoteAction.ListRestoreFiles,Catalog=new(points.Points[0].Time,null)},ct.Token);Assert.That(catalog.Files,Is.Not.Empty);
   File.Delete(Path.Combine(source,"restore-check.bin"));
   using var restored=await client.PostAsJsonAsync($"/api/v1/backup/{id}/restore",new{paths=new[]{Path.Combine(source,"restore-check.bin")},time="now",restore_path=restore,overwrite=true,permissions=false,skip_metadata=true},ct.Token);restored.EnsureSuccessStatusCode();using var restoreTask=JsonDocument.Parse(await restored.Content.ReadAsStringAsync(ct.Token));await WaitTask(client,DuplicatiAdapter.Get(restoreTask.RootElement,"ID").ToString(),ct.Token,password,passphrase);
   var file=Directory.GetFiles(restore,"restore-check.bin",SearchOption.AllDirectories).Single();Assert.That(await File.ReadAllBytesAsync(file,ct.Token),Is.EqualTo(bytes));
   // Recovery on a replacement device: a recovery copy with only destination and passphrase (no local database,
   // sources of the old device) lists versions and restores the identical file straight from the destination.
   var recovery=new ConfigurationAssignment(Guid.NewGuid(),1,new ManagedBackupDefinition("Wiederherstellung",["C:\\Alter-PC\\Daten"],new Uri(destination+Path.DirectorySeparatorChar).AbsoluteUri,passphrase,new(),30,[],null,RestoreOnly:true));
   var recoveryId=await adapter.ApplyConfiguration(recovery,ct.Token);Assert.That(recoveryId,Is.Not.EqualTo(id));
   var recoveryCommand=command with{Id=Guid.NewGuid(),LocalJobId=recoveryId};
   var recoveryPoints=await adapter.ReadCatalog(recoveryCommand with{Action=RemoteAction.ListRestorePoints},ct.Token);Assert.That(recoveryPoints.Points.Length,Is.EqualTo(points.Points.Length));
   var recoveryFiles=await adapter.ReadCatalog(recoveryCommand with{Action=RemoteAction.ListRestoreFiles,Catalog=new(recoveryPoints.Points[0].Time,null)},ct.Token);
   Assert.That(recoveryFiles.Files.Select(x=>x.Path),Has.Some.EndsWith("restore-check.bin"));
   var recovered=Path.Combine(dir,"recovered");
   using(var restoredCopy=await client.PostAsJsonAsync($"/api/v1/backup/{recoveryId}/restore",new{paths=new[]{Path.Combine(source,"restore-check.bin")},time="now",restore_path=recovered,overwrite=false,permissions=false,skip_metadata=true},ct.Token))
   {restoredCopy.EnsureSuccessStatusCode();using var copyTask=JsonDocument.Parse(await restoredCopy.Content.ReadAsStringAsync(ct.Token));await WaitTask(client,DuplicatiAdapter.Get(copyTask.RootElement,"ID").ToString(),ct.Token,password,passphrase);}
   Assert.That(await File.ReadAllBytesAsync(Directory.GetFiles(recovered,"restore-check.bin",SearchOption.AllDirectories).Single(),ct.Token),Is.EqualTo(bytes));
   Assert.That(async()=>await adapter.Dispatch(recoveryCommand with{Action=RemoteAction.RunBackup},options,ct.Token),Throws.InstanceOf<InvalidOperationException>(),"a recovery copy never backs up into the original destination");
  }
  finally
  {
   if(!server.HasExited)server.Kill(true);await server.WaitForExitAsync();await output;await error;Directory.Delete(dir,true);
  }
 }
 private sealed class HistoryFixtureHandler:HttpMessageHandler
 {
  public List<RunHistoryItem> Runs {get;}=[];
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {Runs.AddRange((await request.Content!.ReadFromJsonAsync<HistoryRequest>(ct))!.Runs);return new(HttpStatusCode.NoContent);}
 }
 private sealed class CommandFixtureHandler(SignedCommand envelope):HttpMessageHandler
 {
  public List<CommandReceipt> Receipts {get;}=[];
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   if(request.Method==HttpMethod.Post)Receipts.Add((await request.Content!.ReadFromJsonAsync<CommandReceipt>(ct))!);
   return new(HttpStatusCode.OK){Content=request.Method==HttpMethod.Get?JsonContent.Create(new[]{envelope}):JsonContent.Create(new{})};
  }
 }
 private static async Task WaitTask(HttpClient client,string id,CancellationToken ct,params string[] secrets)
 {
  for(var i=0;i<1000;i++)
  {
   using var task=JsonDocument.Parse(await client.GetStringAsync("/api/v1/task/"+id,ct));var status=DuplicatiAdapter.Get(task.RootElement,"Status").GetString();
   if(status=="Completed")return;if(status=="Failed"){var detail=DuplicatiAdapter.Get(task.RootElement,"ErrorMessage").ToString();foreach(var secret in secrets)detail=detail.Replace(secret,"[REDACTED]");Assert.Fail("Engine task failed: "+detail);}await Task.Delay(100,ct);
  }
  Assert.Fail("Engine task did not finish within timeout");
 }
}
