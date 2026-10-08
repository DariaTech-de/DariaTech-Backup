using System.Security.Cryptography;
using DariaTech.Contracts;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Api;
public sealed record DeploymentInput(Guid DeviceId,Guid ReleaseId);
public static class UpdateApi
{
 public static void MapUpdateApi(this WebApplication app)
 {
  var g=app.MapGroup("/api/v1/management").RequireAuthorization();
  g.MapGet("/agent-releases",async(ManagementDb db)=>await db.AgentReleases.Select(x=>new{x.Id,x.Platform,x.Version,x.Sequence,x.Expires}).ToListAsync());
  g.MapPost("/agent-releases",async(SignedUpdate signed,ManagementDb db,IConfiguration config,HttpContext ctx)=>
  {
   var keyFile=config["Updates:PublicKeyFile"];if(string.IsNullOrEmpty(keyFile))return Results.Problem(statusCode:503,title:"Update trust key is not configured");
   AgentUpdateManifest manifest;
   try{using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(keyFile));manifest=UpdateProtocol.Verify(signed,key,DateTimeOffset.UtcNow);}catch(Exception e)when(e is CryptographicException or FormatException or System.Text.Json.JsonException){return Results.BadRequest();}
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({73941005L})");
   var highest=await db.AgentReleases.Where(x=>x.Platform==manifest.Platform).MaxAsync(x=>(long?)x.Sequence)??0;if(manifest.Sequence<=highest)return Results.Conflict();
   db.AgentReleases.Add(new ApprovedAgentRelease{Id=manifest.ReleaseId,Platform=manifest.Platform,Version=manifest.Version,Sequence=manifest.Sequence,Payload=signed.Payload,Signature=signed.Signature,Expires=manifest.Expires});ManagementApi.Audit(db,ctx,null,"agent.release-approved",manifest.ReleaseId);await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
  g.MapGet("/update-deployments",async(ManagementDb db)=>await db.UpdateDeployments.OrderByDescending(x=>x.Approved).Take(500).ToListAsync());
  g.MapPost("/update-deployments",async(DeploymentInput input,ManagementDb db,HttpContext ctx)=>
  {
   var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==input.DeviceId&&x.Active);var release=await db.AgentReleases.SingleOrDefaultAsync(x=>x.Id==input.ReleaseId&&x.Expires>DateTimeOffset.UtcNow);
   if(device is null||release is null||!await db.Customers.AnyAsync(x=>x.TenantId==device.TenantId&&x.Active))return Results.NotFound();
   var agent=await db.Agents.SingleOrDefaultAsync(x=>x.DeviceId==device.Id&&!x.Revoked);
   if(agent is null||(agent.Platform==""?"win-x64":agent.Platform)!=release.Platform)return Results.Conflict();
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(device.Id.ToByteArray())})");
   if(await db.UpdateDeployments.AnyAsync(x=>x.DeviceId==device.Id&&x.ReleaseId==release.Id))return Results.Conflict();
   var deployment=new UpdateDeployment{TenantId=device.TenantId,DeviceId=device.Id,ReleaseId=release.Id};db.UpdateDeployments.Add(deployment);ManagementApi.Audit(db,ctx,device.TenantId,"agent.update-approved",deployment.Id);await db.SaveChangesAsync();await tx.CommitAsync();return Results.Ok(deployment);
  }).RequireAuthorization("Admin");
  app.MapGet("/api/v1/agent/update",async(ManagementDb db,TenantScope scope,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   var assignment=await (from d in db.UpdateDeployments join r in db.AgentReleases on d.ReleaseId equals r.Id where d.DeviceId==agent.DeviceId&&d.Status=="Approved"&&r.Platform==(agent.Platform==""?"win-x64":agent.Platform)&&r.Expires>DateTimeOffset.UtcNow orderby r.Sequence descending select new{d.Id,r.Payload,r.Signature}).FirstOrDefaultAsync();
   return assignment is null?Results.NoContent():Results.Ok(new UpdateAssignment(assignment.Id,new(assignment.Payload,assignment.Signature)));
  }).RequireRateLimiting("Agent");
  app.MapPost("/api/v1/agent/updates/{id:guid}/receipt",async(Guid id,UpdateReceipt receipt,ManagementDb db,TenantScope scope,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   if(receipt.Status is not ("Downloaded" or "Applying" or "Installed" or "Failed" or "Rejected")||receipt.ErrorCode is not(null or "UpdateRejected" or "InstallationIndeterminate" or "DownloadFailed"))return Results.BadRequest();
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
   var deployment=await db.UpdateDeployments.SingleOrDefaultAsync(x=>x.Id==id&&x.DeviceId==agent.DeviceId);if(deployment is null)return Results.NotFound();
   if(deployment.Status==receipt.Status&&deployment.ErrorCode==receipt.ErrorCode)return Results.NoContent();
   if(deployment.Status is "Installed" or "Failed" or "Rejected")return Results.Conflict();
   if(receipt.Status=="Installed"&&deployment.Status!="Applying"||receipt.Status=="Applying"&&deployment.Status!="Downloaded"||receipt.Status=="Downloaded"&&deployment.Status!="Approved")return Results.Conflict();
   deployment.Status=receipt.Status;deployment.ErrorCode=receipt.ErrorCode;db.Audit.Add(new AuditEvent{TenantId=deployment.TenantId,Actor=agent.DeviceId.ToString(),Action="agent.update-"+receipt.Status.ToLowerInvariant(),Resource=id.ToString()});await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireRateLimiting("Agent");
 }
}
