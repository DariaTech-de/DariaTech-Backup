using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using DariaTech.Console.Data;
using DariaTech.Console.Api;
namespace DariaTech.Console.Pages;
public sealed class LogoutModel(ManagementDb db) : PageModel
{
 public IActionResult OnGet()=>RedirectToPage("/Index");
 public async Task<IActionResult> OnPostAsync(){ManagementApi.Audit(db,HttpContext,null,"logout",Guid.Empty);await db.SaveChangesAsync();await HttpContext.SignOutAsync();return RedirectToPage("/Login");}
}
