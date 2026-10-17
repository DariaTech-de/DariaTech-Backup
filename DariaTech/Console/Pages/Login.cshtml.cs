using System.Security.Claims;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;
[EnableRateLimiting("Login")]
public sealed class LoginModel(ManagementDb db,ISecretStore secrets) : PageModel
{
 [BindProperty]public string Email {get;set;}="";
 [BindProperty]public string Password {get;set;}="";
 [BindProperty]public string Code {get;set;}="";
 public void OnGet(){}
 public async Task<IActionResult> OnPostAsync()
 {
  if(!ModelState.IsValid||Email.Length>200||Password.Length>200||Code.Length!=6){db.Audit.Add(new AuditEvent{Actor="anonymous",Action="login.failed",Resource="console"});await db.SaveChangesAsync();return Failed();}
  var normalized=Email.Trim().ToLowerInvariant();
  await using var tx=await db.Database.BeginTransactionAsync();
  // Serialize account attempts and TOTP consumption across console instances.
  await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))})");
  var user=await db.Users.SingleOrDefaultAsync(x=>x.Email==normalized);
  var now=DateTimeOffset.UtcNow;var valid=false;long? step=null;
  var locked=user is not null&&LoginLock.Locked(user,now);
  if(user is {Active:true}&&!locked)
  {
   var hashResult=UserPasswords.Create().VerifyHashedPassword(user,user.PasswordHash,Password);
   if(hashResult!=PasswordVerificationResult.Failed)
   {
    step=Totp.Validate(secrets.Unprotect(user.TenantId??Guid.Empty,$"totp:{user.Id}",user.TotpSecret),Code,now,user.LastTotpStep);valid=step is not null;
    if(valid&&hashResult==PasswordVerificationResult.SuccessRehashNeeded)user.PasswordHash=UserPasswords.Create().HashPassword(user,Password);
   }
  }
  if(!valid)
  {
   if(user is not null&&!locked)LoginLock.Failed(user,now);
   db.Audit.Add(new AuditEvent{TenantId=user?.TenantId,Actor=user?.Id.ToString()??"anonymous",Action=locked?"login.locked":"login.failed",Resource="console"});
   await db.SaveChangesAsync();await tx.CommitAsync();return Failed();
  }
  user!.FailedLogins=0;user.LockedUntil=null;user.LastTotpStep=step!.Value;
  db.Audit.Add(new AuditEvent{TenantId=user.TenantId,Actor=user.Id.ToString(),Action="login.success",Resource="console"});await db.SaveChangesAsync();await tx.CommitAsync();
  var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,user.Id.ToString()),new(ClaimTypes.Name,user.Email),new(ClaimTypes.Role,user.Role.ToString()),new("session",user.SessionVersion.ToString())};
  if(user.TenantId is {} tenant)claims.Add(new Claim("tenant",tenant.ToString()));
  await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme)));
  return LocalRedirect("/");
 }
 private IActionResult Failed(){ModelState.AddModelError("","Anmeldung fehlgeschlagen.");Password="";Code="";ModelState.Remove(nameof(Password));ModelState.Remove(nameof(Code));return Page();}
}

// Account lock after repeated failures. Attempts during a lock are refused without checking the password and
// neither count nor extend the lock, so the lock ends at a fixed time. When it has ended the counter starts
// again at zero: five further wrong attempts lock the account again for the same period.
public static class LoginLock
{
 public const int MaxFailures=5;public static readonly TimeSpan Duration=TimeSpan.FromMinutes(30);
 public static bool Locked(User user,DateTimeOffset now)
 {
  if(user.LockedUntil is {} until&&until>now)return true;
  if(user.LockedUntil is not null){user.LockedUntil=null;user.FailedLogins=0;}
  return false;
 }
 public static void Failed(User user,DateTimeOffset now){user.FailedLogins++;if(user.FailedLogins>=MaxFailures)user.LockedUntil=now.Add(Duration);}
}
