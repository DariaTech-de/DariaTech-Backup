using DariaTech.Console.Data;
using DariaTech.Contracts;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Services;

public sealed class MonitoringOptions
{
 public int OfflineMinutes { get; set; }=30; public int BackupAgeHours { get; set; }=24;
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
    if(last?.Status is RunStatus.Cancelled or RunStatus.Unknown)problems[$"incomplete:{j.Id}"]=("Warning","BackupNotSuccessful");
   }
   var a=agents.FirstOrDefault(x=>x.DeviceId==d.Id);
   if(Version.TryParse(o.LatestApprovedVersion,out var approved)&&Version.TryParse(a?.Version,out var installed)&&installed<approved)problems["outdated"]=("Warning","AgentOutdated");
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
