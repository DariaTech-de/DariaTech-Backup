using System.Net.Http.Json;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;
public sealed class JobHistorySpool
{
 public long Committed {get;set;} public long Upper {get;set;} public long? Offset {get;set;}
 public List<RunHistoryItem> Captured {get;set;}=[];public List<RunHistoryItem> Pending {get;set;}=[];
}
public sealed partial class DuplicatiAdapter
{
 public async Task CaptureHistory(HttpClient console,JobReport[] jobs,CancellationToken ct)
 {
  var spool=state.Read<Dictionary<string,JobHistorySpool>>("history.bin")??[];
  foreach(var job in jobs)
  {
   if(!spool.TryGetValue(job.LocalId,out var cursor)){cursor=new();spool.Add(job.LocalId,cursor);}
   // Paginate backwards by engine log ID. Preserve continuation and parsed statistics atomically.
   // Bound per-cycle engine work, not the history; a large backlog continues next cycle.
   for(var page=0;page<4;page++)
   {
    var url=$"/api/v1/backup/{Uri.EscapeDataString(job.LocalId)}/log?pagesize=250"+(cursor.Offset is {} offset?$"&offset={offset}":"");
    using var response=await client.GetAsync(url,ct);response.EnsureSuccessStatusCode();using var data=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
    var logs=data.RootElement.EnumerateArray().ToArray();var complete=logs.Length<250;long lowest=long.MaxValue;
    foreach(var log in logs)
    {
     if(!long.TryParse(Get(log,"ID").ToString(),out var id)||id<1)throw new InvalidOperationException("Missing history record identity");
     lowest=Math.Min(lowest,id);cursor.Upper=Math.Max(cursor.Upper,id);
     if(id<=cursor.Committed){complete=true;continue;}
     if(Get(log,"Type").GetString()!="Result")continue;
     var message=Get(log,"Message").GetString();if(message is null)continue;
     try{using var result=JsonDocument.Parse(message);var run=ParseResult(result.RootElement,default);if(run is not null)cursor.Captured.Add(new(job.LocalId,id,run));}catch(JsonException){}
    }
    if(complete)
    {
     cursor.Pending.AddRange(cursor.Captured.OrderBy(x=>x.RecordId));cursor.Captured.Clear();cursor.Committed=cursor.Upper;cursor.Upper=0;cursor.Offset=null;
    }
    else cursor.Offset=lowest;
    state.Write("history.bin",spool);if(complete)break;
   }
  }
  // The cursor and pending batch are one protected atomic file. Advance delivery only after server acceptance.
  for(var batch=0;batch<5;batch++)
  {
   var pending=spool.Values.SelectMany(x=>x.Pending).Take(100).ToArray();if(pending.Length==0)break;
   using var response=await console.PostAsJsonAsync("/api/v1/agent/history",new HistoryRequest(pending),ct);response.EnsureSuccessStatusCode();
   foreach(var item in pending)spool[item.LocalJobId].Pending.Remove(item);
   state.Write("history.bin",spool);
  }
 }
}
