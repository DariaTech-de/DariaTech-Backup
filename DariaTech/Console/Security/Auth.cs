using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using DariaTech.Console.Data;
namespace DariaTech.Console.Security;

public sealed class SessionEvents : CookieAuthenticationEvents
{
 public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
 {
  var db=context.HttpContext.RequestServices.GetRequiredService<ManagementDb>();
  var userId=context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
  var version=context.Principal?.FindFirstValue("session");
  if(!Guid.TryParse(userId,out var id) || !await db.Users.AnyAsync(x=>x.Id==id && x.Active && x.SessionVersion.ToString()==version))
   context.RejectPrincipal();
 }
 public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> c)
 {
  if(c.Request.Path.StartsWithSegments("/api")) {c.Response.StatusCode=401;return Task.CompletedTask;}
  return base.RedirectToLogin(c);
 }
 public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> c)
 {
  c.Response.StatusCode=403;return Task.CompletedTask;
 }
}
public static class Permissions
{
 public const string Operators="SuperAdmin,Administrator,Technician";
 public const string Administrators="SuperAdmin,Administrator";
}
