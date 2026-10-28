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
  g.MapDelete("/managed-jobs/{id:guid}",async(Guid id,ManagementDb db,ISecretStore secrets,HttpContext ctx)=>
   !await db.ManagedJobs.AnyAsync(x=>x.Id==id)?Results.NotFound():await DeleteBackup(id,db,secrets,ctx) is {} error?Results.Conflict(new{error}):Results.NoContent()).RequireAuthorization("Admin");
  app.MapGet("/api/v1/agent/configurations",async(ManagementDb db,TenantScope scope,ISecretStore secrets,HttpContext ctx)=>
  {
   var agent=await DeviceAuthentication.Authenticate(ctx,db,scope);if(agent is null)return Results.Unauthorized();
   // A taken-over job is delivered only once the previous device is known to have stopped backing up.
   var stopped=db.Devices.Where(x=>x.RetireConfirmed!=null||x.RetireManuallyConfirmed!=null).Select(x=>(Guid?)x.Id);
   var jobs=await db.ManagedJobs.Where(x=>x.DeviceId==agent.DeviceId&&x.AppliedRevision<x.LatestRevision&&(x.HandoverFromDeviceId==null||stopped.Contains(x.HandoverFromDeviceId))).OrderBy(x=>x.Id).Take(20).ToListAsync();
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
    job.AppliedRevision=receipt.Revision;job.LocalJobId=receipt.LocalJobId;job.UnreportedSince=DateTimeOffset.UtcNow;
    var backup=await db.Jobs.SingleOrDefaultAsync(x=>x.DeviceId==agent.DeviceId&&x.LocalId==receipt.LocalJobId);
    if(backup is null){backup=new BackupJob{TenantId=job.TenantId,DeviceId=agent.DeviceId,LocalId=receipt.LocalJobId!,Name=job.Name};db.Jobs.Add(backup);}backup.Ownership="Managed";backup.Active=job.Deleted is null;
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
   if(job.Deleted is not null)return Results.Conflict(new{code="BackupDeleted"});
   if(job.LatestRevision!=input.ExpectedRevision)return Results.Conflict(new{code="RevisionConflict"});
   var old=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==id&&x.Revision==job.LatestRevision);
   var definition=Read(secrets,old);
   // Rotating the backup passphrase or destination silently makes the existing chain inaccessible.
   if(definition.RestoreOnly!=input.Definition.RestoreOnly)return Results.Conflict(new{code="RestoreOnlyFixed"});
   if(definition.Passphrase!=input.Definition.Passphrase||definition.TargetUrl!=input.Definition.TargetUrl||!SaasPolicy.SameDirectory(definition.Saas,input.Definition.Saas)||(definition.Proxmox is null)!=(input.Definition.Proxmox is null))return Results.Conflict(new{code="NewBackupChainRequired"});
   // A taken-over job keeps its marker: the agent decides from its local database whether a rebuild is still due.
   var next=input.Definition with{Adopted=definition.Adopted};if(!ConfigurationPolicy.Valid(next))return Results.BadRequest();
   job.LatestRevision++;job.Name=input.Definition.Name;job.Status="Pending";AddRevision(db,secrets,job,next);
   ManagementApi.Audit(db,ctx,job.TenantId,"backup.configuration-changed",job.Id);await db.SaveChangesAsync();await tx.CommitAsync();return Results.Ok(job);
 }
 // Recovery to another device (Acronis "recover to another machine"): the target device gets a copy of the
 // source job with the same destination and passphrase but no schedule. Its engine then lists versions and
 // restores directly from the destination, so this works when the original device is gone.
 public static async Task<(ManagedJob? Job,string? Error)> CreateRecoveryCopy(Guid sourceId,Guid targetDeviceId,ManagementDb db,ISecretStore secrets,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return (null,"Keine Berechtigung.");
  // A deleted backup stays restorable; a recovery copy itself is not a source.
  var source=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.Id==sourceId&&(!x.RestoreOnly||x.Deleted!=null&&x.SourceManagedJobId==null));if(source is null)return (null,"Backup nicht gefunden.");
  var target=await db.Devices.SingleOrDefaultAsync(x=>x.Id==targetDeviceId&&x.Active&&x.TenantId==source.TenantId);
  if(target is null||target.Id==source.DeviceId)return (null,"Bitte ein anderes aktives Gerät desselben Kunden wählen.");
  var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==source.Id&&x.Revision==source.LatestRevision);
  var original=Read(secrets,revision);
  if(original.Saas is not null||original.Proxmox is not null)return (null,"Cloud- und Proxmox-Backups werden über ihre eigene Wiederherstellung zurückgeholt.");
  if(await db.ManagedJobs.AnyAsync(x=>x.DeviceId==target.Id&&x.SourceManagedJobId==source.Id))return (null,"Dieses Backup ist auf dem Gerät bereits zur Wiederherstellung verbunden.");
  var sourceDevice=await db.Devices.Where(x=>x.Id==source.DeviceId).Select(x=>x.Name).SingleAsync();
  var name=$"Wiederherstellung: {original.Name} (von {sourceDevice})";if(name.Length>200)name=name[..200];
  var copy=original with{Name=name,Schedule=null,SourceMounts=null,RestoreOnly=true,Adopted=null};
  if(!ConfigurationPolicy.Valid(copy))return (null,"Die Konfiguration des Backups ist ungültig.");
  var job=new ManagedJob{TenantId=target.TenantId,DeviceId=target.Id,Name=name,LatestRevision=1,RestoreOnly=true,SourceManagedJobId=source.Id};db.ManagedJobs.Add(job);
  AddRevision(db,secrets,job,copy);ManagementApi.Audit(db,ctx,job.TenantId,"backup.recovery-copy-created",job.Id);
  await db.SaveChangesAsync();return (job,null);
 }
 // Replace a device (Acronis "replace machine"): the new device of the same customer takes over the old device's
 // backups with the same destination and passphrase and continues the existing backup chain. The old device is
 // revoked so two devices never write into one destination.
 public static async Task<(int Moved,string? Error)> TakeOver(Guid oldDeviceId,Guid newDeviceId,ManagementDb db,ISecretStore secrets,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return (0,"Keine Berechtigung.");
  await using var tx=await db.Database.BeginTransactionAsync();
  await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(oldDeviceId.ToByteArray())})");
  var target=await db.Devices.SingleOrDefaultAsync(x=>x.Id==newDeviceId&&x.Active);
  var old=await db.Devices.SingleOrDefaultAsync(x=>x.Id==oldDeviceId);
  if(target is null||old is null||old.Id==target.Id||old.TenantId!=target.TenantId)return (0,"Bitte ein anderes Gerät desselben Kunden wählen.");
  var jobs=await db.ManagedJobs.Where(x=>x.DeviceId==old.Id&&!x.RestoreOnly).ToListAsync();
  var moved=0;
  foreach(var job in jobs)
  {
   await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(job.Id.ToByteArray())})");
   var definition=Read(secrets,await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==job.Id&&x.Revision==job.LatestRevision));
   // Cloud and Proxmox jobs depend on the host they were provisioned on; they are set up again on the new device.
   if(definition.Saas is not null||definition.Proxmox is not null)continue;
   var adopted=definition with{Adopted=true};if(!ConfigurationPolicy.Valid(adopted))continue;
   job.DeviceId=target.Id;job.LatestRevision++;job.AppliedRevision=0;job.LastReportedRevision=0;job.LocalJobId=null;job.UnreportedSince=null;job.Status="Pending";job.HandoverFromDeviceId=old.Id;
   AddRevision(db,secrets,job,adopted);ManagementApi.Audit(db,ctx,job.TenantId,"backup.taken-over",job.Id);moved++;
  }
  if(moved==0)return (0,"Auf dem alten Gerät gibt es keine Datei-Backups, die übernommen werden können.");
  // The old agent is not revoked here: it must still be able to learn that it was replaced, pause its own
  // schedules and confirm that. Its credential is revoked when it confirms (AgentApi /retired).
  old.Active=false;old.ReplacedByDeviceId=target.Id;RequestRetirement(old);
  ManagementApi.Audit(db,ctx,old.TenantId,"device.replaced",old.Id);
  await db.SaveChangesAsync();await tx.CommitAsync();return (moved,null);
 }
 public static void RequestRetirement(Device device){device.RetireRequested??=DateTimeOffset.UtcNow;}
 // For a device that will never be online again (wiped, broken, lost): an administrator states that it no
 // longer backs up. This is recorded as a manual statement, not as a technical confirmation; if the device does
 // come back online it is still told to stop.
 // Agents from 0.4 understand the stop (410 Gone) and pause themselves; older agents keep running their local
 // schedules when they come back, so for them only a confirmed local removal is an acceptable statement.
 public static readonly Version FirstRetirementCapableAgent=new(0,4,0);
 public static bool RetirementCapable(string? agentVersion)=>Version.TryParse(agentVersion,out var v)&&v>=FirstRetirementCapableAgent;
 public static async Task<string?> ConfirmRetirementManually(Guid deviceId,ManagementDb db,HttpContext ctx,bool agentRemoved=false)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return "Keine Berechtigung.";
  var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==deviceId);
  if(device is null||device.RetireRequested is null)return "Für dieses Gerät wurde kein Stopp angefordert.";
  if(device.RetireConfirmed is not null||device.RetireManuallyConfirmed is not null)return null;
  var version=await db.Agents.Where(x=>x.DeviceId==deviceId).Select(x=>x.Version).FirstOrDefaultAsync();
  if(!agentRemoved&&!RetirementCapable(version))return $"Der Agent des alten Geräts (Version {(string.IsNullOrEmpty(version)?"unbekannt":version)}) kann sich nicht selbst stoppen und würde nach dem Einschalten weiter sichern. Bitte bestätigen, dass der Agent dort deinstalliert oder das Gerät neu aufgesetzt bzw. entsorgt wurde.";
  if(agentRemoved)ManagementApi.Audit(db,ctx,device.TenantId,"device.agent-removal-confirmed",device.Id);
  device.RetireManuallyConfirmed=DateTimeOffset.UtcNow;device.RetireManuallyConfirmedBy=ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value??"";device.Active=false;
  ManagementApi.Audit(db,ctx,device.TenantId,"device.retire-manually-confirmed",device.Id);await db.SaveChangesAsync();return null;
 }
 // Delete a device without replacement: it leaves lists and monitoring at once and is told to stop its own schedules
 // on its next contact; its credential is revoked when it confirms. A device that never comes back needs nothing
 // more. Its backups stay at the destination and restorable from the customer page.
 public static async Task<string?> DeleteDevice(Guid deviceId,ManagementDb db,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return "Keine Berechtigung.";
  var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==deviceId);if(device is null)return "Gerät nicht gefunden.";
  if(device.Deleted is not null)return null;
  RequestRetirement(device);device.Active=false;device.Deleted=DateTimeOffset.UtcNow;device.DeletedBy=ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value??"";
  foreach(var alert in await db.Alerts.Where(x=>x.DeviceId==device.Id&&x.Resolved==null).ToListAsync())alert.Resolved=device.Deleted;
  ManagementApi.Audit(db,ctx,device.TenantId,"device.deleted",device.Id);await db.SaveChangesAsync();return null;
 }
 // Delete a backup: the device keeps the job without a schedule, so it no longer backs up on its own, and the Console
 // refuses to start it; it leaves lists and monitoring. The backups at the destination are never touched and stay
 // restorable, on the device itself (its local database is kept) or on another device of the customer.
 public static async Task<string?> DeleteBackup(Guid id,ManagementDb db,ISecretStore secrets,HttpContext ctx)
 {
  if(!ctx.User.IsInRole("SuperAdmin")&&!ctx.User.IsInRole("Administrator"))return "Keine Berechtigung.";
  await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(id.ToByteArray())})");
  var job=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.Id==id);if(job is null)return "Backup nicht gefunden.";
  if(job.Deleted is not null)return null;
  var latest=Read(secrets,await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==job.Id&&x.Revision==job.LatestRevision));
  if(latest.Schedule is not null)
  {
   var unscheduled=latest with{Schedule=null};if(!ConfigurationPolicy.Valid(unscheduled))return "Die Konfiguration des Backups ist ungültig.";
   job.LatestRevision++;job.Status="Pending";AddRevision(db,secrets,job,unscheduled);
  }
  job.RestoreOnly=true;job.Deleted=DateTimeOffset.UtcNow;job.DeletedBy=ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value??"";
  if(job.LocalJobId is {} local)foreach(var reported in await db.Jobs.Where(x=>x.DeviceId==job.DeviceId&&x.LocalId==local).ToListAsync())reported.Active=false;
  ManagementApi.Audit(db,ctx,job.TenantId,"backup.deleted",job.Id);await db.SaveChangesAsync();await tx.CommitAsync();return null;
 }
 private static void AddRevision(ManagementDb db,ISecretStore secrets,ManagedJob job,ManagedBackupDefinition definition)
 {
  RecordSchedule(job,definition);
  var r=new ConfigurationRevision{TenantId=job.TenantId,ManagedJobId=job.Id,Revision=job.LatestRevision};
  r.EncryptedConfiguration=secrets.Protect(r.TenantId,$"configuration:{r.ManagedJobId}:{r.Revision}",JsonSerializer.Serialize(definition));db.ConfigurationRevisions.Add(r);
 }
 public static void RecordSchedule(ManagedJob job,ManagedBackupDefinition definition)
 {
  job.ScheduleRecorded=true;job.ScheduleRepeatHours=definition.Schedule?.RepeatHours;job.KeepVersions=definition.KeepVersions;job.ScheduleStart=definition.Schedule?.Start;
  job.ScheduleDays=definition.Schedule?.Days.Aggregate(0,(mask,day)=>mask|1<<(int)day)??0;
 }
 public static ManagedBackupDefinition Read(ISecretStore secrets,ConfigurationRevision r)=>JsonSerializer.Deserialize<ManagedBackupDefinition>(secrets.Unprotect(r.TenantId,$"configuration:{r.ManagedJobId}:{r.Revision}",r.EncryptedConfiguration))!;
}
