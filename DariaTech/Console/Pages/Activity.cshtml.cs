using DariaTech.Console.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;
public sealed class ActivityModel(ManagementDb db):PageModel
{public List<AuditEvent> Items{get;private set;}=[];public async Task OnGetAsync(){Items=await db.Audit.OrderByDescending(x=>x.Id).Take(500).ToListAsync();}}
