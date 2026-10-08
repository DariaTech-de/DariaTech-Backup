using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DariaTech.Agent;
using DariaTech.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
namespace DariaTech.Tests;

[TestFixture,NonParallelizable]
public sealed class NativeUnixTests
{
 [Test,Category("UnixNative")]
 public async Task ManagedUnixEngineSchedulesEncryptedBackupRestoresAndRetainsIdentityAcrossRestart()
 {
  if(OperatingSystem.IsWindows()){Assert.Ignore("Unix native service path");return;}
  var executable=Environment.GetEnvironmentVariable("DARIATECH_ENGINE_NATIVE");
  if(string.IsNullOrWhiteSpace(executable)){Assert.Ignore("Set DARIATECH_ENGINE_NATIVE to a native published OSS engine");return;}
  var root=UnixSecurityTests.TemporaryDirectory();ManagedEngine? engine=null;
  try
  {
   using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4));
   var options=UnixSecurityTests.Options(root);options.ManageEngine=true;options.ExternalEngineExecutable=executable;
   using(var port=new TcpListener(IPAddress.Loopback,0)){port.Start();options.EngineUrl="https://127.0.0.1:"+((IPEndPoint)port.LocalEndpoint).Port;}
   options.RestoreRoot=Path.Combine(root,"restore");UnixPrivatePaths.Directory(options.RestoreRoot,true);options.Validate();
   var state=new ProtectedState(options);var identity=new AgentIdentity(Guid.NewGuid(),Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
   state.Write("identity.bin",identity); // Enrolled identity fixture; no production enrollment substitute.
   var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));state.Write("engine-credential.bin",password);
   engine=new(options,state,NullLogger<ManagedEngine>.Instance);await engine.StartAsync(timeout.Token);
   using var adapter=new DuplicatiAdapter(options,state);
   await WaitForEngine(adapter,timeout.Token);
   var source=Path.Combine(root,"source");Directory.CreateDirectory(source);var storage=Path.Combine(root,"storage");Directory.CreateDirectory(storage);
   var bytes=RandomNumberGenerator.GetBytes(16384);var file=Path.Combine(source,"restore-check.bin");await File.WriteAllBytesAsync(file,bytes,timeout.Token);
   var definition=new ManagedBackupDefinition("Native scheduled Unix backup",[source+Path.DirectorySeparatorChar],new Uri(storage+Path.DirectorySeparatorChar).AbsoluteUri,
    "test-only-encryption-passphrase",new(),30,[],new(DateTimeOffset.UtcNow.AddSeconds(5),1,Enum.GetValues<DayOfWeek>()));
   var id=await adapter.ApplyConfiguration(new(Guid.NewGuid(),1,definition),timeout.Token);
   JobReport? report=null;
   for(var i=0;i<600;i++)
   {
    report=(await adapter.ReadJobs(timeout.Token)).Single(x=>x.LocalId==id);
    if(report.LastRun is {Completed:not null})break;await Task.Delay(200,timeout.Token);
   }
   Assert.That(report?.LastRun?.Status,Is.EqualTo(RunStatus.Success),"The native scheduler must execute without a logged-in desktop session");
   Assert.That(report?.LastRun?.Files,Is.EqualTo(1));
   Assert.That(Directory.GetFiles(storage).All(p=>p.EndsWith(".aes",StringComparison.Ordinal)),Is.True);
   Assert.That(Directory.GetFiles(storage),Is.Not.Empty);
   var command=new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),identity.DeviceId,id,RemoteAction.ListRestorePoints,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5));
   var catalog=await adapter.ReadCatalog(command,timeout.Token);Assert.That(catalog.Points,Is.Not.Empty);
   File.Delete(file);
   var restore=command with{Action=RemoteAction.Restore,Restore=new(catalog.Points[0].Time,[file],"recovered")};
   var task=await adapter.Dispatch(restore,options,timeout.Token);CommandReceipt receipt;
   do{await Task.Delay(100,timeout.Token);receipt=await adapter.TaskReceipt(task,timeout.Token);}while(receipt.Status=="Accepted");
   Assert.That(receipt.Status,Is.EqualTo("Completed"));
   var restored=Directory.GetFiles(options.RestoreRoot,"restore-check.bin",SearchOption.AllDirectories).Single();
   Assert.That(await File.ReadAllBytesAsync(restored,timeout.Token),Is.EqualTo(bytes));
   var tls=state.Read<EngineTlsIdentity>("engine-tls.bin")!;
   await engine.StopAsync(timeout.Token);engine.Dispose();engine=new(options,new ProtectedState(options),NullLogger<ManagedEngine>.Instance);
   await engine.StartAsync(timeout.Token);using var restarted=new DuplicatiAdapter(options,new ProtectedState(options));await WaitForEngine(restarted,timeout.Token);
   Assert.That((await restarted.ReadJobs(timeout.Token)).Single(x=>x.LocalId==id).LastRun?.Status,Is.EqualTo(RunStatus.Success));
   Assert.That(state.Read<AgentIdentity>("identity.bin"),Is.EqualTo(identity));
   Assert.That(state.Read<EngineTlsIdentity>("engine-tls.bin")!.Certificate,Is.EqualTo(tls.Certificate));
   Assert.That(File.GetUnixFileMode(Path.Combine(options.StateDirectory,"engine-cert.pfx")),Is.EqualTo(UnixFileMode.UserRead|UnixFileMode.UserWrite));
  }
  finally{if(engine is not null){await engine.StopAsync(CancellationToken.None);engine.Dispose();}Directory.Delete(root,true);}
 }
 private static async Task WaitForEngine(DuplicatiAdapter adapter,CancellationToken ct)
 {
  for(var i=0;i<150;i++){try{await adapter.ReadJobs(ct);return;}catch(Exception error)when(error is HttpRequestException or InvalidOperationException){await Task.Delay(100,ct);}}
  Assert.Fail("Managed native engine did not authenticate over pinned TLS");
 }
}
