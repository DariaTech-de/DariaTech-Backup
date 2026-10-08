using DariaTech.Console.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;
public sealed class BackupsModel(ManagementDb db):PageModel
{public List<BackupJob> Items{get;private set;}=[];public Dictionary<Guid,string> Devices{get;private set;}=[];public async Task OnGetAsync(){Devices=await db.Devices.Where(x=>x.Active).ToDictionaryAsync(x=>x.Id,x=>x.Name);var active=Devices.Keys.ToArray();Items=await db.Jobs.Where(x=>active.Contains(x.DeviceId)).OrderBy(x=>x.Name).Take(500).ToListAsync();}}
