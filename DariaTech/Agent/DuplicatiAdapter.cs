using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed partial class DuplicatiAdapter : IDisposable
{
 private readonly HttpClient client;
 private readonly ProtectedState state;
 private readonly AgentOptions agentOptions;
 public DuplicatiAdapter(AgentOptions options,ProtectedState store,HttpMessageHandler? handler=null)
 {
  agentOptions=options;state=store;client=new HttpClient(handler??(options.ManageEngine?new EngineReadinessHandler(OperatingSystem.IsWindows()?OwnedEngineConnection.Handler(options,store):EngineTls.Handler(store),store):new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false})){BaseAddress=new Uri(options.EngineUrl),Timeout=TimeSpan.FromSeconds(15)};
 }
 public async Task<JobReport[]> ReadJobs(CancellationToken ct)
 {
  if(client.DefaultRequestHeaders.Authorization is null)
  {
   var credential=state.Read<string>("engine-credential.bin")??throw new InvalidOperationException("Engine credential not provisioned");
   using var auth=await client.PostAsJsonAsync("/api/v1/auth/login",new{Password=credential,RememberMe=false},ct);auth.EnsureSuccessStatusCode();
   using var doc=JsonDocument.Parse(await auth.Content.ReadAsStreamAsync(ct));client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Get(doc.RootElement,"AccessToken").GetString());
  }
  using var response=await client.GetAsync("/api/v1/backups",ct);
  if(response.StatusCode==HttpStatusCode.Unauthorized){client.DefaultRequestHeaders.Authorization=null;throw new HttpRequestException("Engine session expired");}
  response.EnsureSuccessStatusCode();using var data=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  using var scheduleResponse=await client.GetAsync("/api/v1/serverstate",ct);scheduleResponse.EnsureSuccessStatusCode();using var schedule=JsonDocument.Parse(await scheduleResponse.Content.ReadAsStreamAsync(ct));
  var reports=new List<JobReport>();
  foreach(var item in data.RootElement.EnumerateArray())
  {
   var backup=Get(item,"Backup");var id=Get(backup,"ID").ToString();var name=Get(backup,"Name").GetString()??"Backup";
   var meta=Get(backup,"Metadata");RunReport? run=null;
   // Read local result records, never forward their raw Message/Exception fields.
   using var logs=await client.GetAsync($"/api/v1/backup/{Uri.EscapeDataString(id)}/log?pagesize=100",ct);
   if(logs.IsSuccessStatusCode)
   {
    using var logDoc=JsonDocument.Parse(await logs.Content.ReadAsStreamAsync(ct));
    foreach(var log in logDoc.RootElement.EnumerateArray())
    {
     if(Get(log,"Type").GetString()!="Result")continue;
     var message=Get(log,"Message").GetString();if(message is null)continue;
     try{using var result=JsonDocument.Parse(message);run=ParseResult(result.RootElement,meta);if(run is not null)break;}catch(JsonException){ }
    }
   }
   // A hard failure may precede a completed result. Never infer Success from finish timestamps alone.
   var error=Date(Get(meta,"LastErrorDate"));
   if(error is not null&&(run is null||error>run.Completed))run=new RunReport($"engine-error:{error.Value.ToUnixTimeSeconds()}",error.Value,null,RunStatus.Failed,null,null,null,null,"EngineOperationFailed");
   DateTimeOffset? next=null;var proposed=Get(schedule.RootElement,"ProposedSchedule");
   if(proposed.ValueKind==JsonValueKind.Array)foreach(var s in proposed.EnumerateArray())if(Get(s,"Item1").ToString()==id)next=Date(Get(s,"Item2"));
   reports.Add(new JobReport(id,name,next,CloudOutcome(id,run)));
  }
  return reports.ToArray();
 }
 public async Task<ProgressReport?> ReadProgress(CancellationToken ct)
 {
  using var response=await client.GetAsync("/api/v1/progressstate",ct);if(response.StatusCode==HttpStatusCode.NotFound)return null;response.EnsureSuccessStatusCode();
  using var doc=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));var root=doc.RootElement;
  var id=Get(root,"BackupID").ToString();if(string.IsNullOrWhiteSpace(id))return null;
  var fraction=Get(root,"OverallProgress").TryGetDouble(out var f)&&double.IsFinite(f)?Math.Clamp(f,0,1):0;
  return new ProgressReport(id,Number(Get(root,"TaskID")),fraction,Number(Get(root,"ProcessedFileSize")),Number(Get(root,"ProcessedFileCount")));
 }
 private RunReport? CloudOutcome(string localId,RunReport? run)=>run is {Status:RunStatus.Warning}&&((state.Read<Dictionary<string,SaasJobBinding>>("saas-bindings.bin")??[]).ContainsKey(localId)||(state.Read<Dictionary<string,string>>("source-jobs.bin")??[]).ContainsKey(localId))
  ?run with{Status=RunStatus.Failed,ErrorCode="BackupFailed"}:run;
 public static RunReport? ParseResult(JsonElement result,JsonElement metadata)
 {
  var operation=Get(result,"MainOperation").ToString();if(!operation.Equals("Backup",StringComparison.OrdinalIgnoreCase))return null;
  var start=Date(Get(result,"BeginTime"));var end=Date(Get(result,"EndTime"));if(start is null||end is null||end<start)return null;
  var parsed=Get(result,"ParsedResult").ToString();
  var status=parsed.ToLowerInvariant() switch {"success"=>RunStatus.Success,"warning"=>RunStatus.Warning,"error"=>RunStatus.Failed,"fatal"=>RunStatus.Failed,_=>RunStatus.Unknown};
  if(Get(result,"Interrupted").ValueKind==JsonValueKind.True)status=RunStatus.Cancelled;
  var error=status switch {RunStatus.Failed=>"BackupFailed",RunStatus.Warning=>"BackupWarning",RunStatus.Cancelled=>"Cancelled",_=>null};
  return new RunReport($"backup:{start.Value.UtcTicks}",start.Value,end,status,NullableNumber(Get(result,"SizeOfExaminedFiles")),NullableNumber(Get(result,"ExaminedFiles")),NullableNumber(Get(Get(result,"BackendStatistics"),"KnownFileSize"))??NullableNumber(Get(metadata,"TargetFilesSize")),null,error,NullableNumber(Get(Get(result,"BackendStatistics"),"FreeQuotaSpace")),NullableNumber(Get(Get(result,"BackendStatistics"),"TotalQuotaSpace")),Boolean(Get(Get(result,"BackendStatistics"),"ReportedQuotaWarning")),Boolean(Get(Get(result,"BackendStatistics"),"ReportedQuotaError")),OperationError(Get(Get(result,"DeleteResults"),"ParsedResult")));
 }
 public static JsonElement Get(JsonElement e,string name)
 {
  if(e.ValueKind==JsonValueKind.Object)foreach(var p in e.EnumerateObject())if(p.Name.Equals(name,StringComparison.OrdinalIgnoreCase))return p.Value;
  return default;
 }
 private static bool? OperationError(JsonElement e)=>e.ToString() switch {"Error" or "Fatal"=>true,"Success" or "Warning"=>false,_=>null};
 private static bool? Boolean(JsonElement e)=>e.ValueKind is JsonValueKind.True or JsonValueKind.False?e.GetBoolean():null;
 private static long? NullableNumber(JsonElement e)=>long.TryParse(e.ToString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var v)&&v>=0?v:null;
 private static long Number(JsonElement e)=>long.TryParse(e.ToString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var v)?Math.Max(0,v):0;
 private static DateTimeOffset? Date(JsonElement e)
 {
  var value=e.ToString();if(DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date))return date.ToUniversalTime();
  return DateTimeOffset.TryParseExact(value,"yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out date)?date.ToUniversalTime():null;
 }
 public void Dispose()=>client.Dispose();
}
