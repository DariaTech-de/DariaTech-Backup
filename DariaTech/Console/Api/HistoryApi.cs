using DariaTech.Contracts;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Api;
public static class HistoryApi
{
 public static void MapHistoryApi(this WebApplication app)
 {
  app.MapPost("/api/v1/agent/history",async(HistoryRequest request,ManagementDb db,TenantScope scope,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   if(request.Runs is null||request.Runs.Length is <1 or >100||request.Runs.Any(x=>x is null||x.RecordId<1||x.Run is null||!AgentApi.Valid(new("history","history",true,[new(x.LocalJobId,"history",null,x.Run)]))))return Results.BadRequest();
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(agent.DeviceId.ToByteArray())})");
   var localIds=request.Runs.Select(x=>x.LocalJobId).Distinct().ToArray();var jobs=await db.Jobs.Where(x=>x.DeviceId==agent.DeviceId&&localIds.Contains(x.LocalId)).ToListAsync();
   if(jobs.Count!=localIds.Length)return Results.Conflict(new{code="HeartbeatRequired"});
   foreach(var item in request.Runs.DistinctBy(x=>(x.LocalJobId,x.Run.LocalRunId)))
   {
    var job=jobs.Single(x=>x.LocalId==item.LocalJobId);var run=item.Run;
    if(await db.Runs.AnyAsync(x=>x.JobId==job.Id&&x.LocalRunId==run.LocalRunId))continue;
    db.Runs.Add(new BackupRun{TenantId=agent.TenantId,JobId=job.Id,LocalRunId=run.LocalRunId,Started=run.Started,Completed=run.Completed,Status=run.Status,Bytes=run.Bytes,Files=run.Files,StorageBytes=run.StorageBytes,Progress=run.Progress,ErrorCode=run.ErrorCode});
   }
   await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireRateLimiting("Agent");
 }
}
