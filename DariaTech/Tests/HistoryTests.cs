using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DariaTech.Agent;
using DariaTech.Contracts;
using NUnit.Framework;
namespace DariaTech.Tests;
public sealed class HistoryTests
{
 [Test]public async Task HistoryPaginationSurvivesRestartAndNeverForwardsRawLogs()
 {
  if(OperatingSystem.IsWindows())Assert.Ignore("Unix state fixture; Windows DPAPI tested by installer CI");
  var dir=Path.Combine(Path.GetTempPath(),"history-"+Guid.NewGuid());Directory.CreateDirectory(dir);
  try
  {
   var key=Path.Combine(dir,"key");File.WriteAllText(key,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
   var options=new AgentOptions{LinuxKeyFile=key,StateDirectory=Path.Combine(dir,"state")};var state=new ProtectedState(options);
   using var receiver=new HistoryReceiver();using var console=new HttpClient(receiver){BaseAddress=new Uri("https://test-only.invalid")};
   for(var cycle=0;cycle<5;cycle++)
   {
    // Recreate adapter and state every cycle; continuation is on disk, not an in-memory cursor.
    using var adapter=new DuplicatiAdapter(options,new ProtectedState(options),new HistoryEngine());
    await adapter.CaptureHistory(console,[new("1","History",null,null)],CancellationToken.None);
    if(cycle==0)Assert.That(receiver.Runs,Is.Empty);
   }
   Assert.That(receiver.Runs,Has.Count.EqualTo(1003));Assert.That(receiver.Runs.Select(x=>x.RecordId).Distinct().Count(),Is.EqualTo(1003));
   Assert.That(JsonSerializer.Serialize(receiver.Runs),Does.Not.Contain("DO-NOT-FORWARD-password").And.Not.Contain("patient-file-name"));
   var spool=state.Read<Dictionary<string,JobHistorySpool>>("history.bin")!;Assert.That(spool["1"].Pending,Is.Empty);Assert.That(spool["1"].Committed,Is.EqualTo(1003));
  }finally{Directory.Delete(dir,true);}
 }
 private sealed class HistoryEngine:HttpMessageHandler
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   var query=System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);var offset=long.TryParse(query["offset"],out var v)?v:long.MaxValue;
   var now=DateTimeOffset.UtcNow.AddDays(-10);
   var logs=Enumerable.Range(1,1003).Where(id=>id<offset).OrderDescending().Take(250).Select(id=>new{ID=id,Type="Result",Message=JsonSerializer.Serialize(new{MainOperation="Backup",BeginTime=now.AddMinutes(id),EndTime=now.AddMinutes(id).AddSeconds(10),ParsedResult="Success",ExaminedFiles=1,SizeOfExaminedFiles=1024,Errors=new[]{"DO-NOT-FORWARD-password","patient-file-name"}})}).ToArray();
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(logs)});
  }
 }
 private sealed class HistoryReceiver:HttpMessageHandler
 {
  public List<RunHistoryItem> Runs {get;}=[];
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {var batch=await request.Content!.ReadFromJsonAsync<HistoryRequest>(ct);Runs.AddRange(batch!.Runs);return new(HttpStatusCode.NoContent);}
 }
}
