using DariaTech.Console.Data;
using DariaTech.Console.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class DeviceModel(ManagementDb db,IOptions<MonitoringOptions> o,DariaTech.Console.Security.ISecretStore secrets):PageModel
{
 public sealed record ReplaceCandidate(Device Device,int Jobs);
 // Other devices of the same customer whose backups this device can take over; a device with the same name first.
 public List<ReplaceCandidate> Candidates{get;private set;}=[];public ReplaceCandidate? Suggested{get;private set;}
 public string Platform{get;private set;}="";public bool IsMac=>Platform.StartsWith("osx",StringComparison.Ordinal);
 [TempData] public string? Message{get;set;}[TempData] public string? Failure{get;set;}
 private async Task LoadReplace(Device device)
 {
  Platform=await db.Agents.Where(x=>x.DeviceId==device.Id&&!x.Revoked).OrderByDescending(x=>x.Registered).Select(x=>x.Platform).FirstOrDefaultAsync()??"";
  if(!User.IsInRole("SuperAdmin")&&!User.IsInRole("Administrator"))return;
  var counts=await db.ManagedJobs.Where(x=>x.TenantId==device.TenantId&&x.DeviceId!=device.Id&&!x.RestoreOnly).GroupBy(x=>x.DeviceId).Select(g=>new{g.Key,Count=g.Count()}).ToListAsync();
  var ids=counts.Select(x=>x.Key).ToArray();
  var devices=await db.Devices.Where(x=>ids.Contains(x.Id)).ToListAsync();
  Candidates=devices.Select(x=>new ReplaceCandidate(x,counts.Single(c=>c.Key==x.Id).Count)).OrderBy(x=>x.Device.Active).ThenByDescending(x=>x.Device.LastHeartbeat).ToList();
  Suggested=Candidates.FirstOrDefault(x=>string.Equals(x.Device.Name,device.Name,StringComparison.OrdinalIgnoreCase)||!string.IsNullOrEmpty(device.Hostname)&&string.Equals(x.Device.Hostname,device.Hostname,StringComparison.OrdinalIgnoreCase));
 }
 public async Task<IActionResult> OnPostReplaceAsync(Guid id,Guid oldDeviceId,bool confirm)
 {
  if(!confirm){Failure="Bitte bestätigen, dass das alte Gerät nicht mehr sichert.";return RedirectToPage(new{id});}
  var (moved,error)=await DariaTech.Console.Api.ConfigurationApi.TakeOver(oldDeviceId,id,db,secrets,HttpContext);
  if(error is not null)Failure=error;else Message=$"{moved} Backup(s) übernommen. Das alte Gerät wird beim nächsten Kontakt angewiesen, seine Zeitpläne anzuhalten. Erst nach seiner Bestätigung (oder Ihrer Bestätigung, dass es nie wieder online kommt) baut dieses Gerät die Datenbank aus dem Speicherziel auf und sichert im bisherigen Zeitplan weiter.";
  return RedirectToPage(new{id});
 }
 // Handover from a device that has not confirmed its stop yet: this device's taken-over jobs are held back.
 public sealed record Handover(Device Old,int Jobs);
 public List<Handover> PendingHandovers{get;private set;}=[];
 public async Task<IActionResult> OnPostConfirmStoppedAsync(Guid id,Guid oldDeviceId,bool confirm)
 {
  if(!User.IsInRole("SuperAdmin")&&!User.IsInRole("Administrator"))return Forbid();
  if(!confirm){Failure="Bitte bestätigen, dass das alte Gerät nicht mehr sichert und nicht mehr eingeschaltet wird.";return RedirectToPage(new{id});}
  if(!await db.ManagedJobs.AnyAsync(x=>x.DeviceId==id&&x.HandoverFromDeviceId==oldDeviceId)){Failure="Keine Übernahme von diesem Gerät gefunden.";return RedirectToPage(new{id});}
  var error=await DariaTech.Console.Api.ConfigurationApi.ConfirmRetirementManually(oldDeviceId,db,HttpContext);
  if(error is not null)Failure=error;else Message="Bestätigt. Dieses Gerät übernimmt die Backups jetzt. Hinweis: Der Stopp des alten Geräts ist nicht technisch bestätigt – wird es doch wieder eingeschaltet und erreicht die Console, hält es seine Zeitpläne beim ersten Kontakt an.";
  return RedirectToPage(new{id});
 }
 // Planned absence: offline and overdue are not alerted until the end date; afterwards normal monitoring resumes.
 public async Task<IActionResult> OnPostAbsenceAsync(Guid id,DateTime? until,string? note,bool clear)
 {
  if(!User.IsInRole("SuperAdmin")&&!User.IsInRole("Administrator")&&!User.IsInRole("Technician"))return Forbid();
  var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id&&x.Active);if(device is null)return NotFound();
  if(clear){device.AbsentUntil=null;device.AbsenceNote="";DariaTech.Console.Api.ManagementApi.Audit(db,HttpContext,device.TenantId,"device.absence-cleared",device.Id);await db.SaveChangesAsync();Message="Abwesenheit beendet. Es gilt wieder die normale Überwachung.";return RedirectToPage(new{id});}
  var end=until is {} u?new DateTimeOffset(DateTime.SpecifyKind(u.Date.AddDays(1),DateTimeKind.Utc)):(DateTimeOffset?)null;
  if(end is null||end<=DateTimeOffset.UtcNow||end>DateTimeOffset.UtcNow.AddDays(180)||(note?.Length??0)>200){Failure="Bitte ein Enddatum innerhalb der nächsten 180 Tage angeben.";return RedirectToPage(new{id});}
  device.AbsentUntil=end;device.AbsenceNote=note?.Trim()??"";
  DariaTech.Console.Api.ManagementApi.Audit(db,HttpContext,device.TenantId,"device.absence-set",device.Id);await db.SaveChangesAsync();
  Message=$"Abwesend bis {Ui.Date(end)}: „Offline“ und „Backup überfällig“ werden bis dahin nicht gemeldet, Fehler weiterhin. Danach gilt automatisch wieder die normale Überwachung.";
  return RedirectToPage(new{id});
 }
 // The agent was removed from a device that could not confirm its own stop (offline, old agent).
 public async Task<IActionResult> OnPostConfirmRemovedAsync(Guid id,bool confirm)
 {
  if(!User.IsInRole("SuperAdmin")&&!User.IsInRole("Administrator"))return Forbid();
  if(!confirm){Failure="Bitte bestätigen, dass der Agent entfernt wurde.";return RedirectToPage(new{id});}
  var tenant=await db.Devices.Where(x=>x.Id==id).Select(x=>(Guid?)x.TenantId).SingleOrDefaultAsync();if(tenant is null)return NotFound();
  var error=await DariaTech.Console.Api.ConfigurationApi.ConfirmRetirementManually(id,db,HttpContext);
  if(error is not null){Failure=error;return RedirectToPage(new{id});}
  // The device leaves monitoring; its backups stay listed and restorable on the customer page.
  return RedirectToPage("/Customer",null,new{id=await db.Customers.Where(x=>x.TenantId==tenant).Select(x=>x.Id).SingleAsync()},"backups");
 }
 public async Task<IActionResult> OnPostDecommissionAsync(Guid id,bool confirm)
 {
  if(!User.IsInRole("SuperAdmin")&&!User.IsInRole("Administrator"))return Forbid();
  if(!confirm){Failure="Bitte bestätigen, dass das Gerät stillgelegt werden soll.";return RedirectToPage(new{id});}
  var error=await DariaTech.Console.Api.ConfigurationApi.Decommission(id,db,HttpContext);
  if(error is not null)Failure=error;else Message="Stilllegung angefordert. Das Gerät hält seine Zeitpläne beim nächsten Kontakt an und bestätigt das; erst dann wird sein Zugang gesperrt. Seine Backups bleiben im Speicherziel und können auf einem anderen Gerät wiederhergestellt werden.";
  return RedirectToPage(new{id});
 }
 public DeviceRow Row{get;private set;}=null!;public List<ManagedJob> Managed{get;private set;}=[];public Dictionary<Guid,string> Destinations{get;private set;}=[];public List<BackupJob> Jobs{get;private set;}=[];public List<BackupRun> Runs{get;private set;}=[];public List<BackupRun> Recent{get;private set;}=[];public List<Alert> Alerts{get;private set;}=[];
 public async Task<IActionResult> OnGetAsync(Guid id){var row=(await Overview.Load(db,o.Value)).SingleOrDefault(x=>x.Device.Id==id);if(row is null)return NotFound();Row=row;Managed=await db.ManagedJobs.Where(x=>x.DeviceId==id).ToListAsync();foreach(var m in Managed){var r=await db.ConfigurationRevisions.SingleOrDefaultAsync(x=>x.ManagedJobId==m.Id&&x.Revision==m.LatestRevision);if(r is not null)Destinations[m.Id]=DestinationForm.Describe(DariaTech.Console.Api.ConfigurationApi.Read(secrets,r).TargetUrl);}Jobs=await db.Jobs.Where(x=>x.DeviceId==id).ToListAsync();var ids=Jobs.Select(x=>x.Id).ToArray();Runs=await db.Runs.Where(x=>ids.Contains(x.JobId)).OrderByDescending(x=>x.Started).Take(200).ToListAsync();var cutoff=DateTimeOffset.UtcNow.AddDays(-30);Recent=await db.Runs.Where(x=>ids.Contains(x.JobId)&&x.Started>=cutoff).ToListAsync();Alerts=await db.Alerts.Where(x=>x.DeviceId==id&&x.Resolved==null).OrderBy(x=>x.Severity=="Critical"?0:1).ThenByDescending(x=>x.Opened).ToListAsync();await LoadReplace(row.Device);
  var from=Managed.Where(x=>x.HandoverFromDeviceId is not null).GroupBy(x=>x.HandoverFromDeviceId!.Value).ToDictionary(x=>x.Key,x=>x.Count());
  var olds=await db.Devices.Where(x=>from.Keys.Contains(x.Id)).ToListAsync();
  PendingHandovers=olds.Where(x=>x.RetirementPending).Select(x=>new Handover(x,from[x.Id])).ToList();
  Updates=(await (from u in db.UpdateDeployments join r in db.AgentReleases on u.ReleaseId equals r.Id where u.DeviceId==id orderby u.Approved descending select new{u,r.Version}).Take(5).ToListAsync()).Select(x=>(x.u,x.Version)).ToList();
  if(await db.Customers.SingleOrDefaultAsync(x=>x.TenantId==row.Device.TenantId) is {} customer)Protection=(await Onboarding.Load(db,customer,o.Value)).Devices.FirstOrDefault(x=>x.Device.Id==id);
  return Page();}
 public DeviceProtection? Protection{get;private set;}
 public List<(UpdateDeployment Deployment,string Version)> Updates{get;private set;}=[];
}
