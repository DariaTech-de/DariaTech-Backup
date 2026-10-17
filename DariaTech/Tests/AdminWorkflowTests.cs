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
