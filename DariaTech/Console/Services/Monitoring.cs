using DariaTech.Console.Data;
using DariaTech.Contracts;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Services;

public sealed class MonitoringOptions
{
 public int OfflineMinutes { get; set; }=30; public int BackupAgeHours { get; set; }=24;
 public int RepeatedFailureCount {get;set;}=3; public int QuotaCriticalPercent {get;set;}=5; public int QuotaWarningPercent {get;set;}=10;
 public int PollSeconds { get; set; }=60; public string LatestApprovedVersion { get; set; }="";
}
public sealed class Monitoring(IServiceScopeFactory scopes,Microsoft.Extensions.Options.IOptions<MonitoringOptions> configured,ILogger<Monitoring> log) : BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  using var timer=new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(configured.Value.PollSeconds,10,3600)));
  do
  {
   try
   {
    using var s=scopes.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Maintenance=true;var db=s.ServiceProvider.GetRequiredService<ManagementDb>();
    await RecordSchedules(db,s.ServiceProvider.GetRequiredService<Security.ISecretStore>(),stoppingToken);
    await Evaluate(db,configured.Value,DateTimeOffset.UtcNow,stoppingToken);
   }
   catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
   catch(Exception ex){log.LogError("Monitoring iteration failed ({Type})",ex.GetType().Name);}
  }while(await timer.WaitForNextTickAsync(stoppingToken));
 }
 public static async Task Evaluate(ManagementDb db,MonitoringOptions o,DateTimeOffset now,CancellationToken ct=default)
 {
  var releases=await db.AgentReleases.Where(x=>x.Expires>now).OrderByDescending(x=>x.Sequence).ToListAsync(ct);
  var devices=await db.Devices.Where(x=>x.Active).ToListAsync(ct);var jobs=await db.Jobs.Where(x=>x.Active).ToListAsync(ct);
  var managed=await db.ManagedJobs.ToListAsync(ct);
  // Recovery copies only restore; they never run backups and are not backup-health subjects.
  var recoveryCopies=managed.Where(x=>x.RestoreOnly&&x.LocalJobId!=null).Select(x=>(x.DeviceId,x.LocalJobId)).ToHashSet();
  jobs=jobs.Where(j=>!recoveryCopies.Contains((j.DeviceId,j.LocalId))).ToList();
  var managedByJob=managed.Where(x=>!x.RestoreOnly&&x.LocalJobId!=null).GroupBy(x=>(x.DeviceId,x.LocalJobId!)).ToDictionary(x=>x.Key,x=>x.First());
  var facts=await JobFacts.Load(db,jobs.Select(x=>x.Id).ToList(),ct);var jobsByDevice=jobs.ToLookup(x=>x.DeviceId);var managedByDevice=managed.Where(x=>!x.RestoreOnly).ToLookup(x=>x.DeviceId);
  var agents=await db.Agents.ToListAsync(ct);var alerts=await db.Alerts.ToListAsync(ct);
  // Latest update attempt per device: a failed or rejected one stays visible until a newer attempt replaces it.
  var deployments=(await db.UpdateDeployments.ToListAsync(ct)).GroupBy(x=>x.DeviceId).ToDictionary(g=>g.Key,g=>g.OrderByDescending(x=>x.Approved).First());
  var retiring=await db.Devices.Where(x=>x.RetireRequested!=null&&x.RetireConfirmed==null&&x.RetireManuallyConfirmed==null).Select(x=>x.Id).ToListAsync(ct);
  var repeated=Math.Clamp(o.RepeatedFailureCount,2,20);var offlineAfter=now.AddMinutes(-Math.Clamp(o.OfflineMinutes,1,1440));
  foreach(var d in devices)
  {
   var problems=new Dictionary<string,(string Severity,string Code)>();
   // A planned absence suppresses the consequences of being switched off (offline, overdue), not failures.
   var absent=d.AbsentUntil>now;
   var offline=d.LastHeartbeat is null||d.LastHeartbeat<offlineAfter;
   if(offline&&!absent)problems["offline"]=("Critical","DeviceOffline");
   // The engine state is only known from a recent heartbeat; an offline device is reported as offline only.
   if(!offline&&!d.EngineReachable)problems["engine"]=("Critical","EngineUnavailable");
   if(!offline&&d.InventoryIncompleteSince is not null)problems["inventory"]=("Warning","InventoryIncomplete");
   var dj=jobsByDevice[d.Id].ToArray();if(dj.Length==0)problems["no-jobs"]=("Warning","NoBackupJobs");
   foreach(var j in dj)
   {
    var fact=facts[j.Id];var last=fact.Last;var m=managedByJob.GetValueOrDefault((d.Id,j.LocalId));
    var allowed=BackupAge.Allowed(m,o);
    if(allowed is null){if(fact.LastSuccess is null&&!absent)problems[$"manual:{j.Id}"]=("Warning","BackupManualOnly");}
    else if(!absent&&(fact.LastSuccess is null||fact.LastSuccess<now.AddHours(-allowed.Value)))problems[$"stale:{j.Id}"]=("Warning","BackupOverdue");
    if(last?.Status==RunStatus.Failed)problems[$"failed:{j.Id}"]=("Critical",last.ErrorCode=="EngineOperationFailed"?"EngineOperationFailed":"BackupFailed");
    if(last?.Status==RunStatus.Warning)problems[$"warning:{j.Id}"]=("Warning","BackupWarning");
    if(fact.FailuresSinceLastGood>=repeated)problems[$"repeat:{j.Id}"]=("Critical","RepeatedBackupFailures");
    if(last?.RetentionError==true)problems[$"retention:{j.Id}"]=("Critical","RetentionFailed");
    var quota=fact.Quota;
    var free=quota?.QuotaTotalBytes is >0&&quota.QuotaFreeBytes is {} available?100d*available/quota.QuotaTotalBytes.Value:(double?)null;
    if(quota?.QuotaError==true||free is {} critical&&critical<=Math.Clamp(o.QuotaCriticalPercent,1,99))problems[$"capacity:{j.Id}"]=("Critical","StorageCapacityCritical");
    else if(quota?.QuotaWarning==true||free is {} warning&&warning<=Math.Clamp(o.QuotaWarningPercent,1,99))problems[$"capacity:{j.Id}"]=("Warning","StorageCapacityWarning");
    if(fact.Verification=="Failed")problems[$"verification:{j.Id}"]=("Critical","BackupVerificationFailed");
    if(last?.Status is RunStatus.Cancelled or RunStatus.Unknown)problems[$"incomplete:{j.Id}"]=("Warning","BackupNotSuccessful");
   }
   foreach(var m in managedByDevice[d.Id])
   {
    // Distribution failed, or an applied job is gone from the device's complete job list.
    if(m.Status is "Failed" or "Rejected")problems[$"config:{m.Id}"]=("Critical","ConfigurationFailed");
    else if(!offline&&d.EngineReachable&&m.AppliedRevision>0&&m.LocalJobId is {} local&&dj.All(x=>x.LocalId!=local))problems[$"missing:{m.Id}"]=("Critical","ManagedJobMissing");
    if(m.HandoverFromDeviceId is {} from&&retiring.Contains(from))problems["handover"]=("Warning","HandoverPending");
   }
   var a=agents.FirstOrDefault(x=>x.DeviceId==d.Id);
   var platform=a?.Platform is {Length:>0} actual?actual:"win-x64";var approvedVersion=releases.FirstOrDefault(x=>x.Platform==platform)?.Version??(platform=="win-x64"?o.LatestApprovedVersion:"");
   if(Version.TryParse(approvedVersion,out var approved)&&Version.TryParse(a?.Version,out var installed)&&installed<approved)problems["outdated"]=("Warning","AgentOutdated");
   if(deployments.GetValueOrDefault(d.Id) is {Status:"Failed" or "Rejected"} failedUpdate&&releases.FirstOrDefault(x=>x.Id==failedUpdate.ReleaseId) is {} target&&Version.TryParse(target.Version,out var wanted)&&(!Version.TryParse(a?.Version,out var now2)||now2<wanted))problems["update"]=("Warning","AgentUpdateFailed");
   foreach(var (key,value) in problems)
   {
    var alert=alerts.SingleOrDefault(x=>x.DeviceId==d.Id&&x.Key==key);
    if(alert is null){alert=new Alert{TenantId=d.TenantId,DeviceId=d.Id,Key=key,Opened=now};db.Alerts.Add(alert);alerts.Add(alert);}
    if(alert.Resolved is not null){alert.Opened=now;alert.AssignedTo=null;alert.InProgressSince=null;}
    alert.LastSeen=now;alert.Resolved=null;alert.Severity=value.Severity;alert.Code=value.Code;
   }
   foreach(var alert in alerts.Where(x=>x.DeviceId==d.Id&&x.Resolved==null&&!problems.ContainsKey(x.Key)))alert.Resolved=now;
  }
  foreach(var alert in alerts.Where(x=>x.Resolved==null&&devices.All(d=>d.Id!=x.DeviceId)))alert.Resolved=now;
  await db.SaveChangesAsync(ct);
 }
 // Copies the schedule of jobs created before ManagedJob kept a plain copy of it (one-time, idempotent).
 public static async Task<int> RecordSchedules(ManagementDb db,Security.ISecretStore secrets,CancellationToken ct=default)
 {
  var pending=await db.ManagedJobs.Where(x=>!x.ScheduleRecorded||x.ScheduleRepeatHours!=null&&x.ScheduleStart==null).Take(500).ToListAsync(ct);
  foreach(var job in pending)
  {
   var revision=await db.ConfigurationRevisions.SingleOrDefaultAsync(x=>x.ManagedJobId==job.Id&&x.Revision==job.LatestRevision,ct);
   if(revision is not null)Api.ConfigurationApi.RecordSchedule(job,Api.ConfigurationApi.Read(secrets,revision));
   else{job.ScheduleRecorded=true;job.ScheduleRepeatHours=null;}
  }
  await db.SaveChangesAsync(ct);return pending.Count;
 }
}
