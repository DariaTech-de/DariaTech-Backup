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
  g.MapPost("/commands",Request).RequireAuthorization("Operator");
  g.MapPost("/commands/{id:guid}/approve",Approve).RequireAuthorization("Admin");
  app.MapGet("/api/v1/agent/commands",async(ManagementDb db,TenantScope scope,ISecretStore secrets,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   var now=DateTimeOffset.UtcNow;var records=await db.Commands.Where(x=>x.DeviceId==agent.DeviceId&&x.Status=="Pending"&&x.Expires>now).OrderBy(x=>x.Expires).Take(10).ToListAsync();
   return Results.Ok(records.Select(x=>new SignedCommand(secrets.Unprotect(x.TenantId,$"command:{x.Id}",x.EncryptedPayload),x.Signature)));
  }).RequireRateLimiting("Agent");
  app.MapPost("/api/v1/agent/commands/{id:guid}/receipt",async(Guid id,CommandReceipt receipt,ManagementDb db,TenantScope scope,ISecretStore secrets,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   if(receipt.Status is not ("Accepted" or "Completed" or "Failed" or "Indeterminate" or "Rejected")||receipt.TaskId<1||receipt.ErrorCode is not(null or "EngineTaskFailed" or "SourceAccessDenied" or "RepairNeeded" or "PassphraseInvalid" or "TargetFolderMissing" or "TargetLoginFailed" or "TargetUnreachable" or "DispatchIndeterminate" or "PolicyRejected" or "DestinationFolderMissing" or "DestinationHostKeyMismatch" or "DestinationCertificateInvalid" or "DestinationTestFailed"))return Results.BadRequest();
   // Only a host key or certificate fingerprint may accompany a failed destination test.
   if(receipt.Detail is {} detail&&(receipt.ErrorCode is not ("DestinationHostKeyMismatch" or "DestinationCertificateInvalid")||!ManagementApi.Text(detail,300)))return Results.BadRequest();
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
   var c=await db.Commands.SingleOrDefaultAsync(x=>x.Id==id&&x.DeviceId==agent.DeviceId);if(c is null)return Results.NotFound();
   if((receipt.Status is "Accepted" or "Completed")&&receipt.TaskId is null&&receipt.Catalog is null&&!(c.Action==RemoteAction.TestDestination&&receipt.Status=="Completed"))return Results.BadRequest();
   if(receipt.ErrorCode?.StartsWith("Destination",StringComparison.Ordinal)==true&&c.Action!=RemoteAction.TestDestination)return Results.BadRequest();
   if(receipt.Catalog is {} catalog)
   {
    if(c.Action is not (RemoteAction.ListRestorePoints or RemoteAction.ListRestoreFiles or RemoteAction.BrowseFolders)||receipt.Status!="Completed"||catalog.Points is null||catalog.Files is null||catalog.Points.Length>500||catalog.Files.Length>500||catalog.Files.Any(x=>x is null||!ManagementApi.Text(x.Path,1000)||x.Bytes<0)||catalog.Points.Any(x=>x is null||x.Files<0||x.Bytes<0)||System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(catalog).Length>50000)return Results.BadRequest();
   }
   if(receipt.Catalog is {} typed&&(c.Action==RemoteAction.ListRestorePoints&&typed.Files.Length!=0||c.Action is RemoteAction.ListRestoreFiles or RemoteAction.BrowseFolders&&typed.Points.Length!=0||c.Action==RemoteAction.BrowseFolders&&typed.Files.Any(x=>!x.Directory||x.Bytes is not null)))return Results.BadRequest();
   if(c.Status==receipt.Status&&c.TaskId==receipt.TaskId&&c.ErrorCode==receipt.ErrorCode)return Results.NoContent();
   if(c.Status is not ("Pending" or "Accepted"))return Results.Conflict();
   if(c.TaskId is not null&&c.TaskId!=receipt.TaskId)return Results.Conflict();
   if(receipt.Catalog is {} result)c.EncryptedCatalog=secrets.Protect(c.TenantId,$"catalog:{id}",System.Text.Json.JsonSerializer.Serialize(result));
   c.Status=receipt.Status;c.TaskId=receipt.TaskId;c.ErrorCode=receipt.ErrorCode;c.Detail=receipt.Detail;
   db.Audit.Add(new AuditEvent{TenantId=c.TenantId,Actor=agent.DeviceId.ToString(),Action="command."+receipt.Status.ToLowerInvariant(),Resource=id.ToString()});await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireRateLimiting("Agent");
 }
 public static async Task<IResult> Request(CommandInput input,ManagementDb db,CommandSigning signer,ISecretStore secrets,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator")&&!ctx.User.IsInRole("Technician"))return Results.Forbid();
   if(!signer.Enabled)return Results.Problem(statusCode:503,title:"Command signing is not configured");
   if(!Enum.IsDefined(input.Action)||input.ValidMinutes is <1 or >30)return Results.BadRequest();
   if(input.Action is RemoteAction.Restore or RemoteAction.RestoreSaas or RemoteAction.RestoreProxmox&&!ctx.User.IsInRole("Administrator")&&!ctx.User.IsInRole("SuperAdmin"))return Results.Forbid();
   var job=await db.Jobs.SingleOrDefaultAsync(x=>x.Id==input.JobId&&x.Active);if(job is null)return Results.NotFound();
   var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==job.DeviceId&&x.Active);if(device is null||!await db.Customers.AnyAsync(x=>x.TenantId==job.TenantId&&x.Active))return Results.NotFound();
   // A recovery copy shares the original device's destination; backing up into it would mix two devices' data.
   if(input.Action==RemoteAction.RunBackup&&await db.ManagedJobs.AnyAsync(x=>x.DeviceId==job.DeviceId&&x.LocalJobId==job.LocalId&&x.RestoreOnly))return Results.Conflict(new{code="RestoreOnly"});
   if(input.Action==RemoteAction.RestoreSaas)
   {
    var managed=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.DeviceId==job.DeviceId&&x.LocalJobId==job.LocalId);
    if(managed is null||managed.AppliedRevision!=managed.LatestRevision||input.SaasRestore?.ConfigurationRevision!=managed.AppliedRevision)return Results.Conflict(new{code="ConfigurationRevisionRequired"});
    var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==managed.Id&&x.Revision==managed.AppliedRevision);
    if(ConfigurationApi.Read(secrets,revision).Saas is null)return Results.BadRequest();
   }
   if(input.Action==RemoteAction.RestoreProxmox)
   {
    var managed=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.DeviceId==job.DeviceId&&x.LocalJobId==job.LocalId);
    if(managed is null||managed.AppliedRevision!=managed.LatestRevision||input.ProxmoxRestore?.ConfigurationRevision!=managed.AppliedRevision)return Results.Conflict(new{code="ConfigurationRevisionRequired"});
    var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==managed.Id&&x.Revision==managed.AppliedRevision);
    if(ConfigurationApi.Read(secrets,revision).Proxmox is null)return Results.BadRequest();
   }
   var now=DateTimeOffset.UtcNow;var command=new DeviceCommand(Guid.NewGuid(),job.TenantId,job.DeviceId,job.LocalId,input.Action,input.Restore,now,now.AddMinutes(input.ValidMinutes),input.Catalog,input.SaasRestore,input.ProxmoxRestore);
   if(!CommandProtocol.Valid(command,job.DeviceId,now))return Results.BadRequest();
   // Restores need a second administrator's approval; with a single administrator account that would make
   // restores impossible, so then the request is approved by its author and audited as such.
   var restore=command.Action is RemoteAction.Restore or RemoteAction.RestoreSaas or RemoteAction.RestoreProxmox;
   var fourEyes=restore&&await SecondApprovalPossible(db);
   var record=Store(db,signer,secrets,command,job.Id,ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!,fourEyes?"AwaitingApproval":"Pending");if(record is null)return Results.BadRequest();
   ManagementApi.Audit(db,ctx,job.TenantId,"command.requested-"+command.Action.ToString().ToLowerInvariant(),record.Id);
   if(restore&&!fourEyes)ManagementApi.Audit(db,ctx,job.TenantId,"restore.single-administrator",record.Id);
   await db.SaveChangesAsync();return Results.Created($"/api/v1/management/commands/{record.Id}",new{record.Id,record.Status});
 }
 public static async Task<bool> SecondApprovalPossible(ManagementDb db)=>
  await db.Users.CountAsync(x=>x.Active&&x.TenantId==null&&(x.Role==UserRole.SuperAdmin||x.Role==UserRole.Administrator))>=2;
 static RemoteCommand? Store(ManagementDb db,CommandSigning signer,ISecretStore secrets,DeviceCommand command,Guid jobId,string actor,string status)
 {
  var signed=signer.Sign(command);if(signed.Payload.Length>80000)return null;
  var record=new RemoteCommand{Id=command.Id,TenantId=command.TenantId,JobId=jobId,DeviceId=command.DeviceId,RequestedBy=actor,Action=command.Action,Expires=command.Expires,Status=status,Signature=signed.Signature};
  record.EncryptedPayload=secrets.Protect(command.TenantId,$"command:{record.Id}",signed.Payload);db.Commands.Add(record);return record;
 }
 // Monthly verification (like scheduled backup validation in MSP consoles): the engine downloads sample
 // volumes and checks them against its records. Queued by the Console itself; the result raises an alert.
 public const string Scheduler="system:monthly-verification";
 public static async Task<int> QueueDueVerifications(ManagementDb db,CommandSigning signer,ISecretStore secrets,DateTimeOffset now,int offlineMinutes,CancellationToken ct=default)
 {
  var online=now.AddMinutes(-Math.Clamp(offlineMinutes,1,1440));
  var devices=await db.Devices.Where(x=>x.Active&&x.EngineReachable&&x.LastHeartbeat>=online).Select(x=>x.Id).ToListAsync(ct);
  var activeTenants=await db.Customers.Where(x=>x.Active).Select(x=>x.TenantId).ToListAsync(ct);
  var jobs=await db.Jobs.Where(x=>x.Active&&devices.Contains(x.DeviceId)&&activeTenants.Contains(x.TenantId)).ToListAsync(ct);
  var recoveryCopies=await db.ManagedJobs.Where(x=>x.RestoreOnly&&x.LocalJobId!=null).Select(x=>new{x.DeviceId,x.LocalJobId}).ToListAsync(ct);
  var queued=0;
  foreach(var job in jobs)
  {
   if(recoveryCopies.Any(x=>x.DeviceId==job.DeviceId&&x.LocalJobId==job.LocalId))continue;
   if(!await db.Runs.AnyAsync(x=>x.JobId==job.Id&&x.Status==RunStatus.Success,ct))continue;
   var verifications=db.Commands.Where(x=>x.JobId==job.Id&&x.Action==RemoteAction.VerifyBackup);
   // Due 30 days after the last successful check; after a failed, expired or open attempt, retry daily.
   if(await verifications.AnyAsync(x=>x.Status=="Completed"&&x.Expires>now.AddDays(-30),ct)||await verifications.AnyAsync(x=>x.Expires>now.AddDays(-1),ct))continue;
   var command=new DeviceCommand(Guid.NewGuid(),job.TenantId,job.DeviceId,job.LocalId,RemoteAction.VerifyBackup,null,now,now.AddMinutes(30));
   if(!CommandProtocol.Valid(command,job.DeviceId,now)||Store(db,signer,secrets,command,job.Id,Scheduler,"Pending") is not {} record)continue;
   db.Audit.Add(new AuditEvent{TenantId=job.TenantId,Actor=Scheduler,Action="command.requested-verifybackup",Resource=record.Id.ToString()});queued++;
  }
  await db.SaveChangesAsync(ct);return queued;
 }
 // Folder browse and destination test have no backup job yet; they are signed and device-bound like job commands.
 public static async Task<(RemoteCommand? Command,string? Error)> RequestDeviceQuery(Guid deviceId,RemoteAction action,CatalogRequest? catalog,DestinationTest? test,ManagementDb db,CommandSigning signer,ISecretStore secrets,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator")&&!ctx.User.IsInRole("Technician"))return (null,"Keine Berechtigung.");
  if(action is not (RemoteAction.BrowseFolders or RemoteAction.TestDestination))return (null,"Ungültige Aktion.");
  if(!signer.Enabled)return (null,"Die Befehlssignatur ist auf diesem Server nicht eingerichtet.");
  var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==deviceId&&x.Active);
  if(device is null||!await db.Customers.AnyAsync(x=>x.TenantId==device.TenantId&&x.Active))return (null,"Gerät nicht gefunden.");
  var now=DateTimeOffset.UtcNow;var command=new DeviceCommand(Guid.NewGuid(),device.TenantId,device.Id,"0",action,null,now,now.AddMinutes(5),catalog,Test:test);
  if(!CommandProtocol.Valid(command,device.Id,now))return (null,action==RemoteAction.TestDestination?"Ziel oder Zugangsdaten sind unvollständig oder ohne gesicherte Verbindung.":"Ungültiger Pfad.");
  var signed=signer.Sign(command);if(signed.Payload.Length>80000)return (null,"Anfrage zu groß.");
  var record=new RemoteCommand{Id=command.Id,TenantId=device.TenantId,JobId=null,DeviceId=device.Id,RequestedBy=ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!,Action=action,Expires=command.Expires,Status="Pending",Signature=signed.Signature};
  record.EncryptedPayload=secrets.Protect(device.TenantId,$"command:{record.Id}",signed.Payload);db.Commands.Add(record);
  ManagementApi.Audit(db,ctx,device.TenantId,"command.requested-"+action.ToString().ToLowerInvariant(),record.Id);await db.SaveChangesAsync();
  return (record,null);
 }
 public static async Task<IResult> Approve(Guid id,ManagementDb db,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return Results.Forbid();
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
   var command=await db.Commands.SingleOrDefaultAsync(x=>x.Id==id);if(command is null)return Results.NotFound();
   var actor=ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
   if(command.Status!="AwaitingApproval"||command.Expires<=DateTimeOffset.UtcNow||actor==command.RequestedBy)return Results.Conflict();
   command.ApprovedBy=actor;command.Status="Pending";ManagementApi.Audit(db,ctx,command.TenantId,"restore.approved",id);await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
 }

}
