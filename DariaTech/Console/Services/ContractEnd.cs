using System.Security.Claims;
using DariaTech.Console.Api;
using DariaTech.Console.Data;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Services;

// Contract end as separate, deliberate steps. Archiving, locking management access, stopping the devices'
// local backups, revoking storage access, deleting management data and deleting backup archives are different
// things; each one is recorded on its own and none is presented as having done another.
public static class ContractEnd
{
 public sealed record DeviceStop(Device Device,string State);
 public static async Task<List<DeviceStop>> Devices(ManagementDb db,Customer c)=>
  (await db.Devices.Where(x=>x.TenantId==c.TenantId&&(x.RetireRequested!=null||db.Agents.Any(a=>a.DeviceId==x.Id&&!a.Revoked))).OrderBy(x=>x.Name).ToListAsync())
  .Select(d=>new DeviceStop(d,StopState(d))).ToList();
 public static string StopState(Device d)=>d.RetireConfirmed is not null?"Stopp vom Gerät bestätigt":d.RetireManuallyConfirmed is not null?"Stopp manuell bestätigt (nicht technisch)":d.RetireRequested is not null?"Stopp angefordert – noch nicht bestätigt":"sichert weiter";
 static string Actor(HttpContext ctx)=>ctx.User.FindFirstValue(ClaimTypes.Name)??"";
 public static async Task<string?> Run(string step,Customer c,ManagementDb db,HttpContext ctx,DateOnly? end,DateOnly? retain,string? note)
 {
  var today=DateOnly.FromDateTime(DateTime.UtcNow);
  switch(step)
  {
   case "dates":
    if(end is null||retain is null||retain<end||(note?.Length??0)>1000)return "Bitte Vertragsende und ein Aufbewahrungsende angeben, das nicht vor dem Vertragsende liegt.";
    c.ContractEnd=end;c.RetainArchivesUntil=retain;c.HandoverNote=note?.Trim()??"";break;
   case "stop":
    // Also devices of an archived customer (inactive, but their agents still run their local schedules).
    foreach(var d in await db.Devices.Where(x=>x.TenantId==c.TenantId&&x.RetireRequested==null&&db.Agents.Any(a=>a.DeviceId==x.Id&&!a.Revoked)).ToListAsync()){ConfigurationApi.RequestRetirement(d);d.Active=false;}
    break;
   case "lock":
    foreach(var u in await db.Users.Where(x=>x.TenantId==c.TenantId).ToListAsync()){u.Active=false;u.SessionVersion=Guid.NewGuid();}
    c.AccessLocked??=DateTimeOffset.UtcNow;break;
   case "archive":
    c.Active=false;foreach(var d in await db.Devices.Where(x=>x.TenantId==c.TenantId).ToListAsync())d.Active=false;break;
   case "reactivate":
    if(c.Closed is not null)return "Der Vertrag ist abgeschlossen.";c.Active=true;
    // Devices that archiving switched off come back; retired, replaced or revoked devices stay off.
    foreach(var d in await db.Devices.Where(x=>x.TenantId==c.TenantId&&!x.Active&&x.RetireRequested==null&&x.ReplacedByDeviceId==null&&db.Agents.Any(a=>a.DeviceId==x.Id&&!a.Revoked)).ToListAsync())d.Active=true;
    break;
   case "storage":
    c.StorageAccessRevoked??=DateTimeOffset.UtcNow;c.StorageAccessRevokedBy??=Actor(ctx);break;
   case "deletion":
    if(c.RetainArchivesUntil is not {} until||until>today)return "Die Löschung kann erst nach dem vereinbarten Aufbewahrungsende freigegeben werden.";
    c.ArchiveDeletionApproved??=DateTimeOffset.UtcNow;c.ArchiveDeletionApprovedBy??=Actor(ctx);break;
   case "close":
    if(c.Active||c.ContractEnd is null)return "Zum Abschluss muss der Kunde archiviert und ein Vertragsende festgelegt sein.";
    c.Closed??=DateTimeOffset.UtcNow;break;
   default:return "Unbekannter Schritt.";
  }
  ManagementApi.Audit(db,ctx,c.TenantId,"contract."+step,c.Id);await db.SaveChangesAsync();return null;
 }
}
