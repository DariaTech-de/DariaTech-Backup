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
  var managed=(await db.ManagedJobs.Where(x=>!x.RestoreOnly&&x.LocalJobId!=null).ToListAsync()).GroupBy(x=>(x.DeviceId,x.LocalJobId!)).ToDictionary(x=>x.Key,x=>x.First());
  var facts=await JobFacts.Load(db,jobs.Select(x=>x.Id).ToList());var now=DateTimeOffset.UtcNow;var jobsByDevice=jobs.ToLookup(x=>x.DeviceId);
  var list=new List<DeviceRow>();
  foreach(var d in devices)
  {
   var own=jobsByDevice[d.Id].ToArray();var ids=own.Select(x=>x.Id).ToArray();
   var last=ids.Select(x=>facts[x].Last).Where(x=>x is not null).MaxBy(x=>x!.Started);
   var success=ids.Select(x=>facts[x].LastSuccess).Max();
   var next=own.Select(x=>x.NextRun).Min();
   var storage=ids.Sum(x=>facts[x].Storage??0);
   var anyFailed=ids.Any(x=>facts[x].Last?.Status==RunStatus.Failed);var anyWarning=ids.Any(x=>facts[x].Last?.Status is RunStatus.Warning or RunStatus.Cancelled or RunStatus.Unknown);
   // Same age rule as monitoring: per job from its schedule (or explicit limit); manual-only jobs do not count.
   var allRecent=own.All(j=>BackupAge.Allowed(managed.GetValueOrDefault((d.Id,j.LocalId)),o) is not {} hours||facts[j.Id].LastSuccess is {} at&&at>=now.AddHours(-hours)||d.AbsentUntil>now);
   var offline=d.LastHeartbeat is null||d.LastHeartbeat<now.AddMinutes(-o.OfflineMinutes);
   var status=offline?(d.AbsentUntil>now?"Absent":"Offline")
    :!d.EngineReachable||anyFailed||alerts.Any(x=>x.DeviceId==d.Id&&x.Severity=="Critical")?"Critical"
    :ids.Length==0||!allRecent||anyWarning||alerts.Any(x=>x.DeviceId==d.Id)?"Warning":"Healthy";
   list.Add(new(d,customers.SingleOrDefault(x=>x.TenantId==d.TenantId)?.Name??"—",sites.SingleOrDefault(x=>x.Id==d.SiteId)?.Name??"—",agents.SingleOrDefault(x=>x.DeviceId==d.Id)?.Version??"—",status,success,last,next,ids.Length,storage));
  }
  return list.OrderBy(x=>x.Status=="Critical"?0:x.Status=="Offline"?1:x.Status=="Warning"?2:x.Status=="Absent"?3:4).ThenBy(x=>x.Customer).ThenBy(x=>x.Device.Name).ToList();
 }
 public static string Time(DateTimeOffset? t)=>t?.ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'")??"Unbekannt";
 public static string Bytes(long? v)=>v is {} size?$"{size/1024d/1024d/1024d:F2} GiB":"Unbekannt";
}
