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
  if(error is not null)Failure=error;else Message=$"{moved} Backup(s) übernommen. Das Gerät baut jetzt die Datenbank aus dem Speicherziel auf und sichert danach im bisherigen Zeitplan weiter.";
  return RedirectToPage(new{id});
 }
 public DeviceRow Row{get;private set;}=null!;public List<ManagedJob> Managed{get;private set;}=[];public Dictionary<Guid,string> Destinations{get;private set;}=[];public List<BackupJob> Jobs{get;private set;}=[];public List<BackupRun> Runs{get;private set;}=[];public List<BackupRun> Recent{get;private set;}=[];public List<Alert> Alerts{get;private set;}=[];
 public async Task<IActionResult> OnGetAsync(Guid id){var row=(await Overview.Load(db,o.Value)).SingleOrDefault(x=>x.Device.Id==id);if(row is null)return NotFound();Row=row;Managed=await db.ManagedJobs.Where(x=>x.DeviceId==id).ToListAsync();foreach(var m in Managed){var r=await db.ConfigurationRevisions.SingleOrDefaultAsync(x=>x.ManagedJobId==m.Id&&x.Revision==m.LatestRevision);if(r is not null)Destinations[m.Id]=DestinationForm.Describe(DariaTech.Console.Api.ConfigurationApi.Read(secrets,r).TargetUrl);}Jobs=await db.Jobs.Where(x=>x.DeviceId==id).ToListAsync();var ids=Jobs.Select(x=>x.Id).ToArray();Runs=await db.Runs.Where(x=>ids.Contains(x.JobId)).OrderByDescending(x=>x.Started).Take(200).ToListAsync();var cutoff=DateTimeOffset.UtcNow.AddDays(-30);Recent=await db.Runs.Where(x=>ids.Contains(x.JobId)&&x.Started>=cutoff).ToListAsync();Alerts=await db.Alerts.Where(x=>x.DeviceId==id&&x.Resolved==null).OrderBy(x=>x.Severity=="Critical"?0:1).ThenByDescending(x=>x.Opened).ToListAsync();await LoadReplace(row.Device);return Page();}
}
