using DariaTech.Console.Data;
using DariaTech.Console.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class DevicesModel(ManagementDb db,IOptions<MonitoringOptions> options):PageModel
{
 public List<DeviceRow> Rows{get;private set;}=[];
 public async Task OnGetAsync(string? q){Rows=await Overview.Load(db,options.Value);if(!string.IsNullOrWhiteSpace(q))Rows=Rows.Where(x=>(x.Device.Name+" "+x.Device.Hostname+" "+x.Customer+" "+x.Site).Contains(q,StringComparison.OrdinalIgnoreCase)).ToList();}
}
