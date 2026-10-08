using System.Data;
using DariaTech.Contracts;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Api;

public static class AgentApi
{
 public static void MapAgentApi(this WebApplication app)
 {
  app.MapPost("/api/v1/agent/enroll",async(EnrollmentRequest r,ManagementDb db,TenantScope scope,HttpContext ctx)=>
  {
   if(r.Platform is not null&&!UpdateProtocol.Platforms.Contains(r.Platform))return Results.BadRequest();
   if(!ManagementApi.Text(r.Token,64)||r.Token.Length!=64||!ManagementApi.Text(r.Hostname,200)||!ManagementApi.Text(r.OperatingSystem,200)||!ManagementApi.Text(r.AgentVersion,80))return Results.BadRequest();
   await using var tx=await db.Database.BeginTransactionAsync();
   var hash=Tokens.Hash(r.Token);var now=DateTimeOffset.UtcNow;
   // An atomic conditional update is the authority: exactly one request can redeem this token.
   var changed=await db.EnrollmentTokens.IgnoreQueryFilters().Where(x=>x.TokenHash==hash&&x.Used==null&&x.Expires>now).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Used,now));
   if(changed!=1)return Results.Unauthorized();
   var e=await db.EnrollmentTokens.IgnoreQueryFilters().SingleAsync(x=>x.TokenHash==hash);scope.AgentTenant=e.TenantId;
   if(!await db.Customers.AnyAsync(x=>x.TenantId==scope.AgentTenant&&x.Active))return Results.Unauthorized();
   var d=new Device{TenantId=e.TenantId,SiteId=e.SiteId,Name=r.Hostname,Hostname=r.Hostname,OperatingSystem=r.OperatingSystem};var credential=Tokens.Create();
   db.Devices.Add(d);db.Agents.Add(new Agent{TenantId=e.TenantId,DeviceId=d.Id,Version=r.AgentVersion,Platform=r.Platform??"",CredentialHash=Tokens.Hash(credential)});
   db.Audit.Add(new AuditEvent{TenantId=e.TenantId,Actor="enrollment",Action="device.registered",Resource=d.Id.ToString()});
   await db.SaveChangesAsync();await tx.CommitAsync();ctx.Response.Headers.CacheControl="no-store";return Results.Ok(new EnrollmentResponse(d.Id,credential));
  }).RequireRateLimiting("Enrollment");
  app.MapPost("/api/v1/agent/heartbeat",async(HeartbeatRequest r,ManagementDb db,TenantScope scope,HttpContext ctx)=>
  {
   var auth=ctx.Request.Headers.Authorization.ToString();var idHeader=ctx.Request.Headers["X-Device-Id"].ToString();
   if(!auth.StartsWith("Bearer ",StringComparison.Ordinal)||auth.Length!=71||!Guid.TryParse(idHeader,out var id))return Results.Unauthorized();
   var hash=Tokens.Hash(auth[7..]);
   // The only cross-scope read: credential and device ID must BOTH match before assigning scope.
   var a=await db.Agents.IgnoreQueryFilters().SingleOrDefaultAsync(x=>x.DeviceId==id&&x.CredentialHash==hash&&!x.Revoked);
   if(a is null)return Results.Unauthorized();scope.AgentTenant=a.TenantId;
   if(!await db.Customers.AnyAsync(x=>x.TenantId==scope.AgentTenant&&x.Active))return Results.Unauthorized();
   if(!Valid(r)||r.Platform is not null&&a.Platform!=""&&r.Platform!=a.Platform)return Results.BadRequest();
   await using var tx=await db.Database.BeginTransactionAsync();
   await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
   var d=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id&&x.Active);if(d is null)return Results.Unauthorized();
   d.LastHeartbeat=DateTimeOffset.UtcNow;d.EngineReachable=r.EngineReachable;d.OperatingSystem=r.OperatingSystem;a.Version=r.AgentVersion;if(r.Platform is not null)a.Platform=r.Platform;
   d.ActiveJobLocalId=r.ActiveOperation?.LocalJobId;d.Progress=r.ActiveOperation?.Fraction;d.ActiveTaskId=r.ActiveOperation?.TaskId;d.ProgressBytes=r.ActiveOperation?.Bytes??0;d.ProgressFiles=r.ActiveOperation?.Files??0;
   if(r.EngineReachable)
   {
    var jobs=await db.Jobs.Where(x=>x.DeviceId==id).ToListAsync();
    foreach(var job in jobs)job.Active=false;
    foreach(var report in r.Jobs)
    {
     var job=jobs.SingleOrDefault(x=>x.LocalId==report.LocalId);
     if(job is null){job=new BackupJob{TenantId=d.TenantId,DeviceId=id,LocalId=report.LocalId};db.Jobs.Add(job);}
     job.Name=report.Name;job.NextRun=report.NextRun;job.Active=true;
     if(report.LastRun is not {} run)continue;
     var stored=await db.Runs.SingleOrDefaultAsync(x=>x.JobId==job.Id&&x.LocalRunId==run.LocalRunId);
     if(stored is null){stored=new BackupRun{TenantId=d.TenantId,JobId=job.Id,LocalRunId=run.LocalRunId,Started=run.Started};db.Runs.Add(stored);}
     // Terminal runs are immutable; delayed heartbeat cannot turn a completed run back into Running.
     if(stored.Completed is not null||stored.Status is RunStatus.Success or RunStatus.Warning or RunStatus.Failed or RunStatus.Cancelled)continue;
     stored.Status=run.Status;stored.Completed=run.Completed;stored.Bytes=run.Bytes;stored.Files=run.Files;stored.StorageBytes=run.StorageBytes;stored.Progress=run.Progress;stored.ErrorCode=run.ErrorCode;stored.QuotaFreeBytes=run.QuotaFreeBytes;stored.QuotaTotalBytes=run.QuotaTotalBytes;stored.QuotaWarning=run.QuotaWarning;stored.QuotaError=run.QuotaError;stored.RetentionError=run.RetentionError;
    }
   }
   await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireRateLimiting("Agent");
 }
 public static bool Valid(HeartbeatRequest r)
 {
  var now=DateTimeOffset.UtcNow;
  if(r.Platform is not null&&!UpdateProtocol.Platforms.Contains(r.Platform))return false;
  if(!ManagementApi.Text(r.AgentVersion,80)||!ManagementApi.Text(r.OperatingSystem,200)||r.Jobs is null||r.Jobs.Length>500||r.Jobs.Any(x=>x is null)||r.Jobs.Select(x=>x.LocalId).Distinct().Count()!=r.Jobs.Length)return false;
  foreach(var j in r.Jobs)
  {
   if(!ManagementApi.Text(j.LocalId,80)||!ManagementApi.Text(j.Name,200))return false;
   if(j.LastRun is not {} v)continue;
   if(!ManagementApi.Text(v.LocalRunId,100)||!Enum.IsDefined(v.Status)||v.Bytes<0||v.Files<0||v.StorageBytes<0||v.QuotaFreeBytes<0||v.QuotaTotalBytes<0||v.QuotaFreeBytes>v.QuotaTotalBytes&&v.QuotaTotalBytes>0||v.Started>now.AddMinutes(5)||v.Started<now.AddYears(-20)||v.Completed<v.Started||v.Completed>now.AddMinutes(5))return false;
   if(v.Status is RunStatus.Success or RunStatus.Warning or RunStatus.Failed or RunStatus.Cancelled && v.Completed is null && v.ErrorCode!="EngineOperationFailed")return false;
   if(v.Status==RunStatus.Running&&v.Completed is not null)return false;
   if(v.Progress is {} p&&(!double.IsFinite(p)||p<0||p>1))return false;
   if(v.ErrorCode=="EngineOperationFailed"&&(v.Status!=RunStatus.Failed||v.Bytes is not null||v.Files is not null||v.StorageBytes is not null||v.Completed is not null||v.QuotaFreeBytes is not null||v.QuotaTotalBytes is not null||v.QuotaWarning is not null||v.QuotaError is not null||v.RetentionError is not null))return false;
   if(v.ErrorCode is not null&&!new[]{"BackupFailed","BackupWarning","EngineUnavailable","Cancelled","EngineOperationFailed"}.Contains(v.ErrorCode))return false;
  }
  if(r.ActiveOperation is {} progress&&(!r.EngineReachable||!ManagementApi.Text(progress.LocalJobId,80)||progress.TaskId<0||!double.IsFinite(progress.Fraction)||progress.Fraction<0||progress.Fraction>1||progress.Bytes<0||progress.Files<0||r.Jobs.All(x=>x.LocalId!=progress.LocalJobId)))return false;
  return r.EngineReachable||r.Jobs.Length==0;
 }
}
