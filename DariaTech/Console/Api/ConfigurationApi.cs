using System.Text.Json;
using DariaTech.Contracts;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Api;

public static class ConfigurationApi
{
 public static void MapConfigurationApi(this WebApplication app)
 {
  var g=app.MapGroup("/api/v1/management").RequireAuthorization();
  g.MapGet("/managed-jobs",async(ManagementDb db)=>await db.ManagedJobs.ToListAsync());
  g.MapPost("/devices/{deviceId:guid}/managed-jobs",Create).RequireAuthorization("Admin");
  g.MapPut("/managed-jobs/{id:guid}",Update).RequireAuthorization("Admin");
  app.MapGet("/api/v1/agent/configurations",async(ManagementDb db,TenantScope scope,ISecretStore secrets,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   var jobs=await db.ManagedJobs.Where(x=>x.DeviceId==agent.DeviceId&&x.AppliedRevision<x.LatestRevision).OrderBy(x=>x.Id).Take(20).ToListAsync();
   var assignments=new List<ConfigurationAssignment>();
   foreach(var j in jobs){var r=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==j.Id&&x.Revision==j.LatestRevision);assignments.Add(new(j.Id,j.LatestRevision,Read(secrets,r)));}
   return Results.Ok(assignments);
  }).RequireRateLimiting("Agent");
  app.MapPost("/api/v1/agent/configurations/{id:guid}/receipt",async(Guid id,ConfigurationReceipt receipt,ManagementDb db,TenantScope scope,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   if(receipt.Revision<1||receipt.Status is not ("Applied" or "Rejected" or "Failed")||receipt.Status=="Applied"&&(!long.TryParse(receipt.LocalJobId,out var local)||local<1))return Results.BadRequest();
   await using var tx=await db.Database.BeginTransactionAsync();
   // Serialize job adoption with heartbeat/history before taking the configuration lock.
   await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(agent.DeviceId.ToByteArray())})");
   await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
   var job=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.Id==id&&x.DeviceId==agent.DeviceId);if(job is null)return Results.NotFound();
   if(receipt.Revision>job.LatestRevision||receipt.Revision<job.AppliedRevision)return Results.Conflict();
   if(job.LastReportedRevision==receipt.Revision&&job.Status==receipt.Status)return Results.NoContent();
   if(receipt.Status=="Applied")
   {
    if(job.LocalJobId is not null&&job.LocalJobId!=receipt.LocalJobId)return Results.Conflict();
    if(job.AppliedRevision==receipt.Revision)return Results.NoContent();
    job.AppliedRevision=receipt.Revision;job.LocalJobId=receipt.LocalJobId;
    var backup=await db.Jobs.SingleOrDefaultAsync(x=>x.DeviceId==agent.DeviceId&&x.LocalId==receipt.LocalJobId);
    if(backup is null){backup=new BackupJob{TenantId=job.TenantId,DeviceId=agent.DeviceId,LocalId=receipt.LocalJobId!,Name=job.Name};db.Jobs.Add(backup);}backup.Ownership="Managed";
   }
   if(receipt.Revision==job.LatestRevision){job.Status=receipt.Status;job.LastReportedRevision=receipt.Revision;}
   db.Audit.Add(new AuditEvent{TenantId=job.TenantId,Actor=agent.DeviceId.ToString(),Action="backup.configuration-"+receipt.Status.ToLowerInvariant(),Resource=id.ToString()});
   await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireRateLimiting("Agent");
 }
 public static async Task<IResult> Create(Guid deviceId,ConfigurationInput input,ManagementDb db,ISecretStore secrets,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return Results.Forbid();
   if(input.ExpectedRevision!=0||!ConfigurationPolicy.Valid(input.Definition))return Results.BadRequest();
   var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==deviceId&&x.Active);if(device is null)return Results.NotFound();
   var job=new ManagedJob{TenantId=device.TenantId,DeviceId=device.Id,Name=input.Definition.Name,LatestRevision=1};db.ManagedJobs.Add(job);
   AddRevision(db,secrets,job,input.Definition);ManagementApi.Audit(db,ctx,job.TenantId,"backup.configuration-created",job.Id);
   await db.SaveChangesAsync();return Results.Created($"/api/v1/management/managed-jobs/{job.Id}",job);
 }
 public static async Task<IResult> Update(Guid id,ConfigurationInput input,ManagementDb db,ISecretStore secrets,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return Results.Forbid();
   if(input.ExpectedRevision<1||!ConfigurationPolicy.Valid(input.Definition))return Results.BadRequest();
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
   var job=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.Id==id);if(job is null)return Results.NotFound();
   if(job.LatestRevision!=input.ExpectedRevision)return Results.Conflict(new{code="RevisionConflict"});
   var old=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==id&&x.Revision==job.LatestRevision);
   var definition=Read(secrets,old);
   // Rotating the backup passphrase or destination silently makes the existing chain inaccessible.
   if(definition.RestoreOnly!=input.Definition.RestoreOnly)return Results.Conflict(new{code="RestoreOnlyFixed"});
   if(definition.Passphrase!=input.Definition.Passphrase||definition.TargetUrl!=input.Definition.TargetUrl||!SaasPolicy.SameDirectory(definition.Saas,input.Definition.Saas)||(definition.Proxmox is null)!=(input.Definition.Proxmox is null))return Results.Conflict(new{code="NewBackupChainRequired"});
   job.LatestRevision++;job.Name=input.Definition.Name;job.Status="Pending";AddRevision(db,secrets,job,input.Definition);
   ManagementApi.Audit(db,ctx,job.TenantId,"backup.configuration-changed",job.Id);await db.SaveChangesAsync();await tx.CommitAsync();return Results.Ok(job);
 }
 // Recovery to another device (Acronis "recover to another machine"): the target device gets a copy of the
 // source job with the same destination and passphrase but no schedule. Its engine then lists versions and
 // restores directly from the destination, so this works when the original device is gone.
 public static async Task<(ManagedJob? Job,string? Error)> CreateRecoveryCopy(Guid sourceId,Guid targetDeviceId,ManagementDb db,ISecretStore secrets,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return (null,"Keine Berechtigung.");
  var source=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.Id==sourceId&&!x.RestoreOnly);if(source is null)return (null,"Backup nicht gefunden.");
  var target=await db.Devices.SingleOrDefaultAsync(x=>x.Id==targetDeviceId&&x.Active&&x.TenantId==source.TenantId);
  if(target is null||target.Id==source.DeviceId)return (null,"Bitte ein anderes aktives Gerät desselben Kunden wählen.");
  var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==source.Id&&x.Revision==source.LatestRevision);
  var original=Read(secrets,revision);
  if(original.Saas is not null||original.Proxmox is not null)return (null,"Cloud- und Proxmox-Backups werden über ihre eigene Wiederherstellung zurückgeholt.");
  if(await db.ManagedJobs.AnyAsync(x=>x.DeviceId==target.Id&&x.SourceManagedJobId==source.Id))return (null,"Dieses Backup ist auf dem Gerät bereits zur Wiederherstellung verbunden.");
  var sourceDevice=await db.Devices.Where(x=>x.Id==source.DeviceId).Select(x=>x.Name).SingleAsync();
  var name=$"Wiederherstellung: {original.Name} (von {sourceDevice})";if(name.Length>200)name=name[..200];
  var copy=original with{Name=name,Schedule=null,SourceMounts=null,RestoreOnly=true};
  if(!ConfigurationPolicy.Valid(copy))return (null,"Die Konfiguration des Backups ist ungültig.");
  var job=new ManagedJob{TenantId=target.TenantId,DeviceId=target.Id,Name=name,LatestRevision=1,RestoreOnly=true,SourceManagedJobId=source.Id};db.ManagedJobs.Add(job);
  AddRevision(db,secrets,job,copy);ManagementApi.Audit(db,ctx,job.TenantId,"backup.recovery-copy-created",job.Id);
  await db.SaveChangesAsync();return (job,null);
 }
 private static void AddRevision(ManagementDb db,ISecretStore secrets,ManagedJob job,ManagedBackupDefinition definition)
 {
  var r=new ConfigurationRevision{TenantId=job.TenantId,ManagedJobId=job.Id,Revision=job.LatestRevision};
  r.EncryptedConfiguration=secrets.Protect(r.TenantId,$"configuration:{r.ManagedJobId}:{r.Revision}",JsonSerializer.Serialize(definition));db.ConfigurationRevisions.Add(r);
 }
 public static ManagedBackupDefinition Read(ISecretStore secrets,ConfigurationRevision r)=>JsonSerializer.Deserialize<ManagedBackupDefinition>(secrets.Unprotect(r.TenantId,$"configuration:{r.ManagedJobId}:{r.Revision}",r.EncryptedConfiguration))!;
}
