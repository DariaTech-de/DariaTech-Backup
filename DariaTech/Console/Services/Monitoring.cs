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
   try{using var s=scopes.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Maintenance=true;await Evaluate(s.ServiceProvider.GetRequiredService<ManagementDb>(),configured.Value,DateTimeOffset.UtcNow,stoppingToken);}
   catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
   catch(Exception ex){log.LogError("Monitoring iteration failed ({Type})",ex.GetType().Name);}
  }while(await timer.WaitForNextTickAsync(stoppingToken));
 }
 public static async Task Evaluate(ManagementDb db,MonitoringOptions o,DateTimeOffset now,CancellationToken ct=default)
 {
  var approvedVersion=await db.AgentReleases.Where(x=>x.Expires>now).OrderByDescending(x=>x.Sequence).Select(x=>x.Version).FirstOrDefaultAsync(ct)??o.LatestApprovedVersion;
  var devices=await db.Devices.Where(x=>x.Active).ToListAsync(ct);var jobs=await db.Jobs.Where(x=>x.Active).ToListAsync(ct);
  var agents=await db.Agents.ToListAsync(ct);var alerts=await db.Alerts.ToListAsync(ct);
  foreach(var d in devices)
  {
   var problems=new Dictionary<string,(string Severity,string Code)>();
   if(d.LastHeartbeat is null||d.LastHeartbeat<now.AddMinutes(-Math.Clamp(o.OfflineMinutes,1,1440)))problems["offline"]=("Critical","DeviceOffline");
   if(!d.EngineReachable)problems["engine"]=("Critical","EngineUnavailable");
   var dj=jobs.Where(x=>x.DeviceId==d.Id).ToArray();if(dj.Length==0)problems["no-jobs"]=("Warning","NoBackupJobs");
   foreach(var j in dj)
   {
    var last=await db.Runs.Where(x=>x.JobId==j.Id).OrderByDescending(x=>x.Started).FirstOrDefaultAsync(ct);
    var success=await db.Runs.Where(x=>x.JobId==j.Id&&x.Status==RunStatus.Success).MaxAsync(x=>(DateTimeOffset?)x.Completed,ct);
    if(success is null||success<now.AddHours(-Math.Clamp(o.BackupAgeHours,1,8760)))problems[$"stale:{j.Id}"]=("Warning","BackupOverdue");
    if(last?.Status==RunStatus.Failed)problems[$"failed:{j.Id}"]=("Critical",last.ErrorCode=="EngineOperationFailed"?"EngineOperationFailed":"BackupFailed");
    if(last?.Status==RunStatus.Warning)problems[$"warning:{j.Id}"]=("Warning","BackupWarning");
    var recent=await db.Runs.Where(x=>x.JobId==j.Id&&x.Completed!=null).OrderByDescending(x=>x.Started).Take(Math.Clamp(o.RepeatedFailureCount,2,20)).Select(x=>x.Status).ToArrayAsync(ct);
    if(recent.Length==Math.Clamp(o.RepeatedFailureCount,2,20)&&recent.All(x=>x==RunStatus.Failed))problems[$"repeat:{j.Id}"]=("Critical","RepeatedBackupFailures");
    if(last?.RetentionError==true)problems[$"retention:{j.Id}"]=("Critical","RetentionFailed");
    var quota=await db.Runs.Where(x=>x.JobId==j.Id&&(x.QuotaError!=null||x.QuotaTotalBytes>0)).OrderByDescending(x=>x.Started).FirstOrDefaultAsync(ct);
    var free=quota?.QuotaTotalBytes is >0&&quota.QuotaFreeBytes is {} available?100d*available/quota.QuotaTotalBytes.Value:(double?)null;
    if(quota?.QuotaError==true||free is {} critical&&critical<=Math.Clamp(o.QuotaCriticalPercent,1,99))problems[$"capacity:{j.Id}"]=("Critical","StorageCapacityCritical");
    else if(quota?.QuotaWarning==true||free is {} warning&&warning<=Math.Clamp(o.QuotaWarningPercent,1,99))problems[$"capacity:{j.Id}"]=("Warning","StorageCapacityWarning");
    var verification=await db.Commands.Where(x=>x.JobId==j.Id&&x.Action==RemoteAction.VerifyBackup&&(x.Status=="Completed"||x.Status=="Failed")).OrderByDescending(x=>x.Expires).FirstOrDefaultAsync(ct);
    if(verification?.Status=="Failed")problems[$"verification:{j.Id}"]=("Critical","BackupVerificationFailed");
    if(last?.Status is RunStatus.Cancelled or RunStatus.Unknown)problems[$"incomplete:{j.Id}"]=("Warning","BackupNotSuccessful");
   }
   var a=agents.FirstOrDefault(x=>x.DeviceId==d.Id);
   if(Version.TryParse(approvedVersion,out var approved)&&Version.TryParse(a?.Version,out var installed)&&installed<approved)problems["outdated"]=("Warning","AgentOutdated");
   foreach(var (key,value) in problems)
   {
    var alert=alerts.SingleOrDefault(x=>x.DeviceId==d.Id&&x.Key==key);
    if(alert is null){alert=new Alert{TenantId=d.TenantId,DeviceId=d.Id,Key=key,Opened=now};db.Alerts.Add(alert);alerts.Add(alert);}
    if(alert.Resolved is not null)alert.Opened=now;
    alert.LastSeen=now;alert.Resolved=null;alert.Severity=value.Severity;alert.Code=value.Code;
   }
   foreach(var alert in alerts.Where(x=>x.DeviceId==d.Id&&x.Resolved==null&&!problems.ContainsKey(x.Key)))alert.Resolved=now;
  }
  foreach(var alert in alerts.Where(x=>x.Resolved==null&&devices.All(d=>d.Id!=x.DeviceId)))alert.Resolved=now;
  await db.SaveChangesAsync(ct);
 }
}
