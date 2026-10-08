using DariaTech.Console.Data;
using DariaTech.Console.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class DeviceModel(ManagementDb db,IOptions<MonitoringOptions> o,DariaTech.Console.Security.ISecretStore secrets):PageModel
{
 public DeviceRow Row{get;private set;}=null!;public List<ManagedJob> Managed{get;private set;}=[];public Dictionary<Guid,string> Destinations{get;private set;}=[];public List<BackupJob> Jobs{get;private set;}=[];public List<BackupRun> Runs{get;private set;}=[];public List<BackupRun> Recent{get;private set;}=[];public List<Alert> Alerts{get;private set;}=[];
 public async Task<IActionResult> OnGetAsync(Guid id){var row=(await Overview.Load(db,o.Value)).SingleOrDefault(x=>x.Device.Id==id);if(row is null)return NotFound();Row=row;Managed=await db.ManagedJobs.Where(x=>x.DeviceId==id).ToListAsync();foreach(var m in Managed){var r=await db.ConfigurationRevisions.SingleOrDefaultAsync(x=>x.ManagedJobId==m.Id&&x.Revision==m.LatestRevision);if(r is not null)Destinations[m.Id]=DestinationForm.Describe(DariaTech.Console.Api.ConfigurationApi.Read(secrets,r).TargetUrl);}Jobs=await db.Jobs.Where(x=>x.DeviceId==id).ToListAsync();var ids=Jobs.Select(x=>x.Id).ToArray();Runs=await db.Runs.Where(x=>ids.Contains(x.JobId)).OrderByDescending(x=>x.Started).Take(200).ToListAsync();var cutoff=DateTimeOffset.UtcNow.AddDays(-30);Recent=await db.Runs.Where(x=>ids.Contains(x.JobId)&&x.Started>=cutoff).ToListAsync();Alerts=await db.Alerts.Where(x=>x.DeviceId==id&&x.Resolved==null).OrderBy(x=>x.Severity=="Critical"?0:1).ThenByDescending(x=>x.Opened).ToListAsync();return Page();}
}
