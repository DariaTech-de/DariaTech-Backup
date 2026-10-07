using DariaTech.Console.Data;
using DariaTech.Console.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class DeviceModel(ManagementDb db,IOptions<MonitoringOptions> o):PageModel
{
 public DeviceRow Row{get;private set;}=null!;public List<BackupJob> Jobs{get;private set;}=[];public List<BackupRun> Runs{get;private set;}=[];
 public async Task<IActionResult> OnGetAsync(Guid id){var row=(await Overview.Load(db,o.Value)).SingleOrDefault(x=>x.Device.Id==id);if(row is null)return NotFound();Row=row;Jobs=await db.Jobs.Where(x=>x.DeviceId==id).ToListAsync();var ids=Jobs.Select(x=>x.Id).ToArray();Runs=await db.Runs.Where(x=>ids.Contains(x.JobId)).OrderByDescending(x=>x.Started).Take(200).ToListAsync();return Page();}
}
