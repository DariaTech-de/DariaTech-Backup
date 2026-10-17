using DariaTech.Console.Data;
using DariaTech.Contracts;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Services;

// Per-job facts from the run history, loaded with a fixed number of set-based queries instead of one query per
// job, so monitoring and overview pages cost the same for five devices and for five thousand.
public sealed record JobFact(BackupRun? Last,DateTimeOffset? LastSuccess,long? Storage,BackupRun? Quota,int FailuresSinceLastGood,string? Verification);
public static class JobFacts
{
 public static async Task<Dictionary<Guid,JobFact>> Load(ManagementDb db,IReadOnlyCollection<Guid> jobs,CancellationToken ct=default)
 {
  if(jobs.Count==0)return [];
  var ids=jobs.ToList();var runs=db.Runs.Where(x=>ids.Contains(x.JobId));
  var last=await runs.GroupBy(x=>x.JobId).Select(g=>g.OrderByDescending(x=>x.Started).First()).ToDictionaryAsync(x=>x.JobId,ct);
  var success=await runs.Where(x=>x.Status==RunStatus.Success).GroupBy(x=>x.JobId).Select(g=>new{g.Key,Last=g.Max(x=>x.Completed)}).ToDictionaryAsync(x=>x.Key,x=>x.Last,ct);
  var storage=await runs.Where(x=>x.StorageBytes!=null).GroupBy(x=>x.JobId).Select(g=>g.OrderByDescending(x=>x.Started).Select(x=>new{x.JobId,x.StorageBytes}).First()).ToDictionaryAsync(x=>x.JobId,x=>x.StorageBytes,ct);
  var quota=await runs.Where(x=>x.QuotaError!=null||x.QuotaTotalBytes>0).GroupBy(x=>x.JobId).Select(g=>g.OrderByDescending(x=>x.Started).First()).ToDictionaryAsync(x=>x.JobId,ct);
  // Repeated failures: completed failed runs after the newest completed run that did not fail.
  var good=await runs.Where(x=>x.Completed!=null&&x.Status!=RunStatus.Failed).GroupBy(x=>x.JobId).Select(g=>new{g.Key,Last=g.Max(x=>x.Started)}).ToDictionaryAsync(x=>x.Key,x=>x.Last,ct);
  var failed=await runs.Where(x=>x.Completed!=null&&x.Status==RunStatus.Failed).Select(x=>new{x.JobId,x.Started}).ToListAsync(ct);
  var streaks=failed.GroupBy(x=>x.JobId).ToDictionary(g=>g.Key,g=>g.Count(x=>!good.TryGetValue(g.Key,out var since)||x.Started>since));
  var verification=await db.Commands.Where(x=>x.JobId!=null&&ids.Contains(x.JobId.Value)&&x.Action==RemoteAction.VerifyBackup&&(x.Status=="Completed"||x.Status=="Failed"))
   .GroupBy(x=>x.JobId!.Value).Select(g=>g.OrderByDescending(x=>x.Expires).Select(x=>new{JobId=x.JobId!.Value,x.Status}).First()).ToDictionaryAsync(x=>x.JobId,x=>x.Status,ct);
  return ids.Distinct().ToDictionary(id=>id,id=>new JobFact(last.GetValueOrDefault(id),success.GetValueOrDefault(id),storage.GetValueOrDefault(id),quota.GetValueOrDefault(id),streaks.GetValueOrDefault(id),verification.GetValueOrDefault(id)));
 }
}

// How old the last successful backup of a job may be before it is overdue.
public static class BackupAge
{
 public const int GraceHours=6;
 // Hours: allowed age, or null when the job runs manually only (no schedule and no explicit limit).
 public static double? Allowed(ManagedJob? managed,MonitoringOptions o)
 {
  if(managed?.MaxBackupAgeHours is int explicitHours)return explicitHours;
  if(managed is not {ScheduleRecorded:true})return Math.Clamp(o.BackupAgeHours,1,8760); // locally managed or not yet recorded
  if(managed.ScheduleRepeatHours is not int repeat)return null;
  return LongestGapHours(repeat,managed.ScheduleDays,managed.ScheduleStart)+GraceHours;
 }
 // Longest gap between two runs as the engine schedules them (Duplicati Scheduler.GetNextValidTime): advance
 // by the repeat interval; a run on a day without runs moves day by day (interval of a day or more) or by
 // further intervals (shorter intervals) to the next allowed day. Days are evaluated in German local time;
 // the start determines the alignment. Without a known start the conservative estimate below is used.
 public static double LongestGapHours(int repeatHours,int days,DateTimeOffset? start)
 {
  if(start is not {} first||days is 0 or 127)return LongestGapHours(repeatHours,days);
  bool Allowed(DateTimeOffset t)=>(days&1<<(int)TimeZoneInfo.ConvertTime(t,Ui.Zone).DayOfWeek)!=0;
  DateTimeOffset Roll(DateTimeOffset t)
  {
   if(repeatHours>=24){for(var n=0;n<8&&!Allowed(t);n++)t=t.AddDays(1);}
   else for(var n=0;n<200&&!Allowed(t);n++)t=t.AddHours(repeatHours);
   return t;
  }
  var run=Roll(first);var end=run.AddDays(28);double longest=0;
  for(var i=0;i<10000&&run<end;i++){var next=Roll(run.AddHours(repeatHours));longest=Math.Max(longest,(next-run).TotalHours);run=next;}
  return longest>0?longest:LongestGapHours(repeatHours,days);
 }
 // Longest regular interval between two scheduled runs: the repeat interval, stretched over days without runs
 // (Mon-Fri daily: Friday to Monday is 72 hours, so a Monday morning is not overdue).
 public static double LongestGapHours(int repeatHours,int days)
 {
  if(days is 0 or 127)return repeatHours;
  var allowed=Enumerable.Range(0,7).Where(d=>(days&1<<d)!=0).ToArray();
  var longest=allowed.Select((d,i)=>(allowed[(i+1)%allowed.Length]-d+7)%7 is var gap&&gap==0?7:gap).Max();
  // A run that would fall on a day without runs moves to the next allowed day.
  return repeatHours>=24?repeatHours+(longest-1)*24:longest*24;
 }
 public static string Describe(ManagedJob? managed)
 {
  if(managed is null||!managed.ScheduleRecorded)return "";
  if(managed.ScheduleRepeatHours is not int repeat)return "Manuell (kein Zeitplan)";
  var every=repeat%24==0?(repeat==24?"täglich":repeat==168?"wöchentlich":$"alle {repeat/24} Tage"):$"alle {repeat} Std.";
  var names=new[]{"So","Mo","Di","Mi","Do","Fr","Sa"};var days=managed.ScheduleDays;
  return days is 0 or 127?every:$"{every} · {string.Join(", ",Enumerable.Range(1,7).Select(x=>x%7).Where(d=>(days&1<<d)!=0).Select(d=>names[d]))}";
 }
}
