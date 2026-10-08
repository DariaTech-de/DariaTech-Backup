using System.Security.Claims;
using DariaTech.Contracts;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Api;
public static class CommandApi
{
 public static void MapCommandApi(this WebApplication app)
 {
  var g=app.MapGroup("/api/v1/management").RequireAuthorization();
  g.MapGet("/commands",async(ManagementDb db)=>await db.Commands.OrderByDescending(x=>x.Expires).Take(500).Select(x=>new{x.Id,x.TenantId,x.DeviceId,x.JobId,x.Action,x.Status,x.Expires,x.TaskId,x.ErrorCode,x.RequestedBy,x.ApprovedBy}).ToListAsync());
  g.MapGet("/commands/{id:guid}/catalog",async(Guid id,ManagementDb db,ISecretStore secrets,HttpContext ctx)=>
  {
   var command=await db.Commands.SingleOrDefaultAsync(x=>x.Id==id);if(command is null||command.EncryptedCatalog is null)return Results.NotFound();
   ManagementApi.Audit(db,ctx,command.TenantId,"restore.catalog-viewed",id);await db.SaveChangesAsync();
   return Results.Ok(System.Text.Json.JsonSerializer.Deserialize<RestoreCatalog>(secrets.Unprotect(command.TenantId,$"catalog:{id}",command.EncryptedCatalog)));
  }).RequireAuthorization("Operator");
  g.MapPost("/commands",async(CommandInput input,ManagementDb db,CommandSigning signer,ISecretStore secrets,HttpContext ctx)=>
  {
   if(!signer.Enabled)return Results.Problem(statusCode:503,title:"Command signing is not configured");
   if(!Enum.IsDefined(input.Action)||input.ValidMinutes is <1 or >30)return Results.BadRequest();
   if(input.Action is RemoteAction.Restore or RemoteAction.RestoreSaas&&!ctx.User.IsInRole("Administrator")&&!ctx.User.IsInRole("SuperAdmin"))return Results.Forbid();
   var job=await db.Jobs.SingleOrDefaultAsync(x=>x.Id==input.JobId&&x.Active);if(job is null)return Results.NotFound();
   var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==job.DeviceId&&x.Active);if(device is null||!await db.Customers.AnyAsync(x=>x.TenantId==job.TenantId&&x.Active))return Results.NotFound();
   if(input.Action==RemoteAction.RestoreSaas)
   {
    var managed=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.DeviceId==job.DeviceId&&x.LocalJobId==job.LocalId);
    if(managed is null||managed.AppliedRevision!=managed.LatestRevision||input.SaasRestore?.ConfigurationRevision!=managed.AppliedRevision)return Results.Conflict(new{code="ConfigurationRevisionRequired"});
    var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==managed.Id&&x.Revision==managed.AppliedRevision);
    if(ConfigurationApi.Read(secrets,revision).Saas is null)return Results.BadRequest();
   }
   var now=DateTimeOffset.UtcNow;var command=new DeviceCommand(Guid.NewGuid(),job.TenantId,job.DeviceId,job.LocalId,input.Action,input.Restore,now,now.AddMinutes(input.ValidMinutes),input.Catalog,input.SaasRestore);
   if(!CommandProtocol.Valid(command,job.DeviceId,now))return Results.BadRequest();
   var signed=signer.Sign(command);if(signed.Payload.Length>80000)return Results.BadRequest();var record=new RemoteCommand{Id=command.Id,TenantId=job.TenantId,JobId=job.Id,DeviceId=job.DeviceId,RequestedBy=ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!,Action=command.Action,Expires=command.Expires,Status=command.Action is RemoteAction.Restore or RemoteAction.RestoreSaas?"AwaitingApproval":"Pending",Signature=signed.Signature};
   record.EncryptedPayload=secrets.Protect(job.TenantId,$"command:{record.Id}",signed.Payload);db.Commands.Add(record);ManagementApi.Audit(db,ctx,job.TenantId,"command.requested-"+command.Action.ToString().ToLowerInvariant(),record.Id);
   await db.SaveChangesAsync();return Results.Created($"/api/v1/management/commands/{record.Id}",new{record.Id,record.Status});
  }).RequireAuthorization("Operator");
  g.MapPost("/commands/{id:guid}/approve",async(Guid id,ManagementDb db,HttpContext ctx)=>
  {
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
   var command=await db.Commands.SingleOrDefaultAsync(x=>x.Id==id);if(command is null)return Results.NotFound();
   var actor=ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
   if(command.Status!="AwaitingApproval"||command.Expires<=DateTimeOffset.UtcNow||actor==command.RequestedBy)return Results.Conflict();
   command.ApprovedBy=actor;command.Status="Pending";ManagementApi.Audit(db,ctx,command.TenantId,"restore.approved",id);await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireAuthorization("Admin");
  app.MapGet("/api/v1/agent/commands",async(ManagementDb db,TenantScope scope,ISecretStore secrets,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   var now=DateTimeOffset.UtcNow;var records=await db.Commands.Where(x=>x.DeviceId==agent.DeviceId&&x.Status=="Pending"&&x.Expires>now).OrderBy(x=>x.Expires).Take(10).ToListAsync();
   return Results.Ok(records.Select(x=>new SignedCommand(secrets.Unprotect(x.TenantId,$"command:{x.Id}",x.EncryptedPayload),x.Signature)));
  }).RequireRateLimiting("Agent");
  app.MapPost("/api/v1/agent/commands/{id:guid}/receipt",async(Guid id,CommandReceipt receipt,ManagementDb db,TenantScope scope,ISecretStore secrets,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   if(receipt.Status is not ("Accepted" or "Completed" or "Failed" or "Indeterminate" or "Rejected")||receipt.TaskId<1||receipt.ErrorCode is not(null or "EngineTaskFailed" or "DispatchIndeterminate" or "PolicyRejected"))return Results.BadRequest();
   if((receipt.Status is "Accepted" or "Completed") && receipt.TaskId is null&&receipt.Catalog is null)return Results.BadRequest();
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
   var c=await db.Commands.SingleOrDefaultAsync(x=>x.Id==id&&x.DeviceId==agent.DeviceId);if(c is null)return Results.NotFound();
   if(receipt.Catalog is {} catalog)
   {
    if(c.Action is not (RemoteAction.ListRestorePoints or RemoteAction.ListRestoreFiles)||receipt.Status!="Completed"||catalog.Points is null||catalog.Files is null||catalog.Points.Length>500||catalog.Files.Length>500||catalog.Files.Any(x=>x is null||!ManagementApi.Text(x.Path,1000)||x.Bytes<0)||catalog.Points.Any(x=>x is null||x.Files<0||x.Bytes<0)||System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(catalog).Length>50000)return Results.BadRequest();
   }
   if(receipt.Catalog is {} typed&&(c.Action==RemoteAction.ListRestorePoints&&typed.Files.Length!=0||c.Action==RemoteAction.ListRestoreFiles&&typed.Points.Length!=0))return Results.BadRequest();
   if(c.Status==receipt.Status&&c.TaskId==receipt.TaskId&&c.ErrorCode==receipt.ErrorCode)return Results.NoContent();
   if(c.Status is not ("Pending" or "Accepted"))return Results.Conflict();
   if(c.TaskId is not null&&c.TaskId!=receipt.TaskId)return Results.Conflict();
   if(receipt.Catalog is {} result)c.EncryptedCatalog=secrets.Protect(c.TenantId,$"catalog:{id}",System.Text.Json.JsonSerializer.Serialize(result));
   c.Status=receipt.Status;c.TaskId=receipt.TaskId;c.ErrorCode=receipt.ErrorCode;
   db.Audit.Add(new AuditEvent{TenantId=c.TenantId,Actor=agent.DeviceId.ToString(),Action="command."+receipt.Status.ToLowerInvariant(),Resource=id.ToString()});await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireRateLimiting("Agent");
 }
}
