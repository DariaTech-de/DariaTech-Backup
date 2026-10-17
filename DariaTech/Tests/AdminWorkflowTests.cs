using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DariaTech.Agent;
using DariaTech.Contracts;
using NUnit.Framework;
namespace DariaTech.Tests;

// Agent-side admin workflow guarantees that need no engine or database.
public sealed class AdminWorkflowTests
{
 static (AgentOptions Options,ProtectedState State,string Directory) Agent()
 {
  var directory=Path.Combine(Path.GetTempPath(),"dt-admin-"+Guid.NewGuid());Directory.CreateDirectory(directory);
  var key=Path.Combine(directory,"agent.key");File.WriteAllText(key,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
  if(!OperatingSystem.IsWindows())File.SetUnixFileMode(key,UnixFileMode.UserRead|UnixFileMode.UserWrite);
  using var signing=ECDsa.Create(ECCurve.NamedCurves.nistP256);var pem=Path.Combine(directory,"commands.pem");File.WriteAllText(pem,signing.ExportSubjectPublicKeyInfoPem());
  var options=new AgentOptions{StateDirectory=Path.Combine(directory,"state"),LinuxKeyFile=key,AllowRemoteCommands=true,CommandPublicKeyFile=pem,EngineUrl="https://127.0.0.1:9"};
  return (options,new ProtectedState(options),directory);
 }
 sealed class ReceiptConsole(Func<Guid,HttpStatusCode> answer):HttpMessageHandler
 {
  public List<Guid> Posted {get;}=[];
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   if(request.Method==HttpMethod.Get)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(Array.Empty<SignedCommand>())});
   var id=Guid.Parse(request.RequestUri!.Segments[^2].TrimEnd('/'));Posted.Add(id);
   return Task.FromResult(new HttpResponseMessage(answer(id)));
  }
 }
 static DeviceCommand Command(Guid device)=>new(Guid.NewGuid(),Guid.NewGuid(),device,"1",RemoteAction.RunBackup,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(10));

 [Test]public async Task APermanentlyRefusedReceiptNeverBlocksOtherCommandsAndIsNeverExecutedAgain()
 {
  var (options,state,directory)=Agent();
  try
  {
   var device=Guid.NewGuid();var deleted=Command(device);var conflicting=Command(device);var flaky=Command(device);var fine=Command(device);
   var journal=new Dictionary<Guid,CommandJournalEntry>
   {
    [deleted.Id]=new(deleted,"a",Guid.NewGuid(),new("Completed",5,null),false),
    [conflicting.Id]=new(conflicting,"b",Guid.NewGuid(),new("Failed",6,"EngineTaskFailed"),false),
    [flaky.Id]=new(flaky,"c",Guid.NewGuid(),new("Completed",7,null),false),
    [fine.Id]=new(fine,"d",Guid.NewGuid(),new("Rejected",null,"PolicyRejected"),false),
   };
   state.Write("commands.bin",journal);
   var answers=new Dictionary<Guid,HttpStatusCode>{[deleted.Id]=HttpStatusCode.NotFound,[conflicting.Id]=HttpStatusCode.Conflict,[flaky.Id]=HttpStatusCode.ServiceUnavailable,[fine.Id]=HttpStatusCode.NoContent};
   var handler=new ReceiptConsole(id=>answers[id]);using var console=new HttpClient(handler){BaseAddress=new Uri("https://console.test-only.invalid")};
   using var adapter=new DuplicatiAdapter(options,state,new HttpClientHandler());var logged=new List<string>();
   await RemoteCommands.Synchronize(console,adapter,options,state,device,CancellationToken.None,logged.Add);
   Assert.That(handler.Posted,Is.EquivalentTo(new[]{deleted.Id,conflicting.Id,flaky.Id,fine.Id}),"every journal entry is attempted even after a refusal");
   var after=state.Read<Dictionary<Guid,CommandJournalEntry>>("commands.bin")!;
   Assert.That(after[deleted.Id].Refused,Is.EqualTo(404));Assert.That(after[conflicting.Id].Refused,Is.EqualTo(409));
   Assert.That(after[flaky.Id].Reported,Is.False,"a transient failure is retried later");Assert.That(after[fine.Id].Reported,Is.True);
   Assert.That(logged,Has.Count.EqualTo(2));Assert.That(string.Join("\n",logged),Does.Not.Contain(deleted.TenantId.ToString()));
   handler.Posted.Clear();answers[flaky.Id]=HttpStatusCode.NoContent;
   await RemoteCommands.Synchronize(console,adapter,options,state,device,CancellationToken.None,logged.Add);
   Assert.That(handler.Posted,Is.EqualTo(new[]{flaky.Id}),"refused and delivered receipts are not sent again");
   Assert.That(state.Read<Dictionary<Guid,CommandJournalEntry>>("commands.bin")!.Keys,Is.EquivalentTo(journal.Keys),"entries stay for replay protection until they expire");
  }
  finally{Directory.Delete(directory,true);}
 }
 [Test]public async Task ARevokedAgentStopsTheCommandCycleInsteadOfMarkingReceiptsRefused()
 {
  var (options,state,directory)=Agent();
  try
  {
   var device=Guid.NewGuid();var command=Command(device);
   state.Write("commands.bin",new Dictionary<Guid,CommandJournalEntry>{[command.Id]=new(command,"a",Guid.NewGuid(),new("Completed",5,null),false)});
   using var console=new HttpClient(new ReceiptConsole(_=>HttpStatusCode.Unauthorized)){BaseAddress=new Uri("https://console.test-only.invalid")};
   using var adapter=new DuplicatiAdapter(options,state,new HttpClientHandler());
   Assert.ThrowsAsync<HttpRequestException>(()=>RemoteCommands.Synchronize(console,adapter,options,state,device,CancellationToken.None));
   Assert.That(state.Read<Dictionary<Guid,CommandJournalEntry>>("commands.bin")![command.Id].Reported,Is.False);
  }
  finally{Directory.Delete(directory,true);}
 }
}

public sealed class LoginLockTests
{
 [Test]public void AttemptsDuringALockNeitherCountNorExtendItAndTheCounterRestartsAfterwards()
 {
  var user=new DariaTech.Console.Data.User();var start=new DateTimeOffset(2026,10,1,8,0,0,TimeSpan.Zero);
  for(var i=0;i<4;i++){Assert.That(DariaTech.Console.Pages.LoginLock.Locked(user,start),Is.False);DariaTech.Console.Pages.LoginLock.Failed(user,start);}
  Assert.That(user.LockedUntil,Is.Null);
  DariaTech.Console.Pages.LoginLock.Failed(user,start);var until=user.LockedUntil;Assert.That(until,Is.EqualTo(start.AddMinutes(30)));
  // Attempts while locked: the page skips verification and does not call Failed, so the end stays fixed.
  for(var i=1;i<=20;i++)Assert.That(DariaTech.Console.Pages.LoginLock.Locked(user,start.AddMinutes(i)),Is.True);
  Assert.That(user.LockedUntil,Is.EqualTo(until));Assert.That(user.FailedLogins,Is.EqualTo(5));
  Assert.That(DariaTech.Console.Pages.LoginLock.Locked(user,start.AddMinutes(30)),Is.False,"the lock ends exactly at its end time");
  Assert.That(user.FailedLogins,Is.Zero);Assert.That(user.LockedUntil,Is.Null);
  DariaTech.Console.Pages.LoginLock.Failed(user,start.AddMinutes(31));Assert.That(user.LockedUntil,Is.Null,"one failure after the lock does not lock again");
 }
}

public sealed class RetirementTests
{
 sealed class FakeEngineAndConsole:HttpMessageHandler
 {
  public bool Paused;public bool Running=true;public int Confirmations;public HttpStatusCode ConfirmAnswer=HttpStatusCode.NoContent;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   var path=request.RequestUri!.AbsolutePath;HttpResponseMessage Json(object o)=>new(HttpStatusCode.OK){Content=JsonContent.Create(o)};
   return Task.FromResult(path switch
   {
    "/api/v1/auth/login"=>Json(new{AccessToken="test-only-token"}),
    "/api/v1/backups"=>Json(Array.Empty<object>()),
    "/api/v1/serverstate"=>Json(new{ProgramState=Paused?"Paused":"Running",EstimatedPauseEnd="0001-01-01T00:00:00Z",ProposedSchedule=Array.Empty<object>()}),
    "/api/v1/serverstate/pause"=>Pause(),
    "/api/v1/progressstate"=>Running?Json(new{BackupID="1",TaskID=4,OverallProgress=0.5}):new HttpResponseMessage(HttpStatusCode.NotFound),
    "/api/v1/agent/retired"=>Confirm(),
    _=>new HttpResponseMessage(HttpStatusCode.NotFound)
   });
   HttpResponseMessage Pause(){Paused=request.RequestUri!.Query.Contains("duration=")?false:true;return new(HttpStatusCode.OK);}
   HttpResponseMessage Confirm(){Confirmations++;return new(ConfirmAnswer);}
  }
 }
 [Test]public async Task AReplacedDevicePausesIndefinitelyAndConfirmsOnlyWhenNothingRuns()
 {
  var directory=Path.Combine(Path.GetTempPath(),"dt-retire-"+Guid.NewGuid());Directory.CreateDirectory(directory);
  try
  {
   var key=Path.Combine(directory,"agent.key");File.WriteAllText(key,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
   if(!OperatingSystem.IsWindows())File.SetUnixFileMode(key,UnixFileMode.UserRead|UnixFileMode.UserWrite);
   var options=new AgentOptions{StateDirectory=Path.Combine(directory,"state"),LinuxKeyFile=key,EngineUrl="https://127.0.0.1:9"};var state=new ProtectedState(options);state.Write("engine-credential.bin","test-only-engine-password");
   var fake=new FakeEngineAndConsole();using var adapter=new DuplicatiAdapter(options,state,fake);using var console=new HttpClient(fake,false){BaseAddress=new Uri("https://console.test-only.invalid")};
   var log=new List<string>();
   Assert.That(await Retirement.Enforce(console,adapter,state,CancellationToken.None,log.Add),Is.False,"a running backup finishes before the stop is confirmed");
   Assert.That(fake.Paused,Is.True,"new scheduled runs are prevented at once, without a time limit");Assert.That(fake.Confirmations,Is.Zero);Assert.That(Retirement.Active(state),Is.True);
   fake.Running=false;fake.ConfirmAnswer=HttpStatusCode.ServiceUnavailable;
   Assert.That(await Retirement.Enforce(console,adapter,state,CancellationToken.None,log.Add),Is.False,"an undelivered confirmation is retried");
   fake.ConfirmAnswer=HttpStatusCode.NoContent;fake.Paused=false; // e.g. the engine restarted
   Assert.That(await Retirement.Enforce(console,adapter,state,CancellationToken.None,log.Add),Is.True);Assert.That(fake.Paused,Is.True,"the pause is re-applied");
   Assert.That(await Retirement.Enforce(console,adapter,state,CancellationToken.None,log.Add),Is.True);Assert.That(fake.Confirmations,Is.EqualTo(2),"confirmed once delivered, then only the pause is kept");
  }
  finally{Directory.Delete(directory,true);}
 }
}
