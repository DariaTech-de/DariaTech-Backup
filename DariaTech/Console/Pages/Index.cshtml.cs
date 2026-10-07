using DariaTech.Console.Data;
using DariaTech.Console.Services;
using DariaTech.Contracts;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class IndexModel(ManagementDb db,IOptions<MonitoringOptions> options):PageModel
{
 public List<DeviceRow> Rows {get;private set;}=[];public int Customers{get;private set;}public int Successes{get;private set;}
 public async Task OnGetAsync(){Rows=await Overview.Load(db,options.Value);Customers=await db.Customers.CountAsync();var since=DateTimeOffset.UtcNow.AddHours(-24);Successes=await db.Runs.CountAsync(x=>x.Status==RunStatus.Success&&x.Completed>=since);}
}
