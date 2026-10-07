using DariaTech.Console.Data;
using DariaTech.Contracts;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Services;
public sealed record DeviceRow(Device Device,string Customer,string Site,string Version,string Status,DateTimeOffset? LastSuccess,BackupRun? LastRun,DateTimeOffset? NextRun,int Jobs,long Storage);
public static class Overview
{
 public static async Task<List<DeviceRow>> Load(ManagementDb db,MonitoringOptions o)
 {
  var devices=await db.Devices.Where(x=>x.Active).ToListAsync();var customers=await db.Customers.ToListAsync();var sites=await db.Sites.ToListAsync();var agents=await db.Agents.ToListAsync();var jobs=await db.Jobs.Where(x=>x.Active).ToListAsync();var alerts=await db.Alerts.Where(x=>x.Resolved==null).ToListAsync();
  var list=new List<DeviceRow>();
  foreach(var d in devices)
  {
   var ids=jobs.Where(x=>x.DeviceId==d.Id).Select(x=>x.Id).ToArray();
   var last=await db.Runs.Where(x=>ids.Contains(x.JobId)).OrderByDescending(x=>x.Started).FirstOrDefaultAsync();
   var success=await db.Runs.Where(x=>ids.Contains(x.JobId)&&x.Status==RunStatus.Success).MaxAsync(x=>(DateTimeOffset?)x.Completed);
   var next=jobs.Where(x=>x.DeviceId==d.Id).Select(x=>x.NextRun).Min();
   var storage=0L;var allRecent=true;var anyFailed=false;var anyWarning=false;
   foreach(var jid in ids)
   {
    var latest=await db.Runs.Where(x=>x.JobId==jid).OrderByDescending(x=>x.Started).FirstOrDefaultAsync();
    var knownStorage=await db.Runs.Where(x=>x.JobId==jid&&x.StorageBytes!=null).OrderByDescending(x=>x.Started).Select(x=>x.StorageBytes).FirstOrDefaultAsync();storage+=knownStorage??0;anyFailed|=latest?.Status==RunStatus.Failed;anyWarning|=latest?.Status is RunStatus.Warning or RunStatus.Cancelled or RunStatus.Unknown;
    var recentSuccess=await db.Runs.Where(x=>x.JobId==jid&&x.Status==RunStatus.Success).MaxAsync(x=>(DateTimeOffset?)x.Completed);
    allRecent&=recentSuccess is not null&&recentSuccess>=DateTimeOffset.UtcNow.AddHours(-o.BackupAgeHours);
   }
   var status=d.LastHeartbeat is null||d.LastHeartbeat<DateTimeOffset.UtcNow.AddMinutes(-o.OfflineMinutes)?"Offline"
    :!d.EngineReachable||anyFailed||alerts.Any(x=>x.DeviceId==d.Id&&x.Severity=="Critical")?"Critical"
    :ids.Length==0||!allRecent||anyWarning||alerts.Any(x=>x.DeviceId==d.Id)?"Warning":"Healthy";
   list.Add(new(d,customers.SingleOrDefault(x=>x.TenantId==d.TenantId)?.Name??"—",sites.SingleOrDefault(x=>x.Id==d.SiteId)?.Name??"—",agents.SingleOrDefault(x=>x.DeviceId==d.Id)?.Version??"—",status,success,last,next,ids.Length,storage));
  }
  return list.OrderBy(x=>x.Status=="Critical"?0:x.Status=="Offline"?1:x.Status=="Warning"?2:3).ThenBy(x=>x.Customer).ThenBy(x=>x.Device.Name).ToList();
 }
 public static string Time(DateTimeOffset? t)=>t?.ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'")??"Unbekannt";
 public static string Bytes(long? v)=>v is {} size?$"{size/1024d/1024d/1024d:F2} GiB":"Unbekannt";
}
