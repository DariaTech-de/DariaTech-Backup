using DariaTech.Console.Data;
using DariaTech.Console.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
// Work list: what needs attention, how old the last good backup is, what to do and who is on it.
public sealed class AlertsModel(ManagementDb db,IOptions<MonitoringOptions> options):PageModel
{
 public List<Alert> Items{get;private set;}=[];public List<Alert> Resolved{get;private set;}=[];
 public Dictionary<Guid,DeviceRow> Devices{get;private set;}=[];public Dictionary<Guid,string> Names{get;private set;}=[];
 public bool CanWork=>User.IsInRole("SuperAdmin")||User.IsInRole("Administrator")||User.IsInRole("Technician");
 public string Me=>User.Identity?.Name??"";
 public async Task OnGetAsync()
 {
  Items=await db.Alerts.Where(x=>x.Resolved==null).OrderBy(x=>x.Severity=="Critical"?0:1).ThenByDescending(x=>x.Opened).Take(500).ToListAsync();
  var since=DateTimeOffset.UtcNow.AddDays(-7);Resolved=await db.Alerts.Where(x=>x.Resolved>=since).OrderByDescending(x=>x.Resolved).Take(100).ToListAsync();
  Devices=(await Overview.Load(db,options.Value)).ToDictionary(x=>x.Device.Id);Names=await db.Devices.ToDictionaryAsync(x=>x.Id,x=>x.Name);
 }
 public async Task<IActionResult> OnPostTakeAsync(Guid alertId,bool release)
 {
  if(!CanWork)return Forbid();
  var alert=await db.Alerts.SingleOrDefaultAsync(x=>x.Id==alertId&&x.Resolved==null);if(alert is null)return RedirectToPage();
  if(release){if(alert.AssignedTo==Me){alert.AssignedTo=null;alert.InProgressSince=null;}}
  else{alert.AssignedTo=Me;alert.InProgressSince=DateTimeOffset.UtcNow;}
  DariaTech.Console.Api.ManagementApi.Audit(db,HttpContext,alert.TenantId,release?"alert.released":"alert.taken",alert.Id);await db.SaveChangesAsync();return RedirectToPage();
 }
}
