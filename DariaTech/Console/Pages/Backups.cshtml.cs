using DariaTech.Console.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;
public sealed class BackupsModel(ManagementDb db):PageModel
{public List<BackupJob> Items{get;private set;}=[];public Dictionary<Guid,string> Devices{get;private set;}=[];public async Task OnGetAsync(){Items=await db.Jobs.OrderBy(x=>x.Name).Take(500).ToListAsync();Devices=await db.Devices.ToDictionaryAsync(x=>x.Id,x=>x.Name);}}
