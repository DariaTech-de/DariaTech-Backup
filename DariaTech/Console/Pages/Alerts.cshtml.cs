using DariaTech.Console.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;
public sealed class AlertsModel(ManagementDb db):PageModel
{public List<Alert> Items{get;private set;}=[];public Dictionary<Guid,string> Devices{get;private set;}=[];public async Task OnGetAsync(){Items=await db.Alerts.Where(x=>x.Resolved==null).OrderByDescending(x=>x.Opened).Take(500).ToListAsync();Devices=await db.Devices.ToDictionaryAsync(x=>x.Id,x=>x.Name);}}
