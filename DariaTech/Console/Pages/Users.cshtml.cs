using DariaTech.Console.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;
[Authorize(Policy="Admin")]
public sealed class UsersModel(ManagementDb db):PageModel
{public List<User> Items{get;private set;}=[];public async Task OnGetAsync(){Items=await db.Users.OrderBy(x=>x.Email).ToListAsync();}}
