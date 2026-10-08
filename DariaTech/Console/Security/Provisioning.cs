using DariaTech.Console.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Security;
public static class Provisioning
{
 public static async Task CreateUser(ManagementDb db,ISecretStore secrets,IConfiguration config)
 {
  var email=config["Provision:Email"]?.Trim().ToLowerInvariant()??throw new InvalidOperationException("Provision:Email required");
  var password=File.ReadAllText(config["Provision:PasswordFile"]??throw new InvalidOperationException("Provision:PasswordFile required")).TrimEnd('\r','\n');
  var output=config["Provision:TotpOutputFile"]??throw new InvalidOperationException("Provision:TotpOutputFile required");
  if(!email.Contains('@')||email.Length>200||password.Length<14||password.Length>200)throw new InvalidOperationException("Valid email and a 14+ character password required");
  if(!Enum.TryParse<UserRole>(config["Provision:Role"]??"SuperAdmin",out var role)||!Enum.IsDefined(role))throw new InvalidOperationException("Invalid role");
  Guid? tenant=Guid.TryParse(config["Provision:TenantId"],out var id)?id:null;
  if((role is UserRole.CustomerAdmin or UserRole.CustomerReadOnly)!=(tenant is not null))throw new InvalidOperationException("Customer roles require a tenant; operator roles must not have a tenant");
  if(tenant is not null&&!await db.Tenants.AnyAsync(x=>x.Id==tenant))throw new InvalidOperationException("Tenant not found");
  if(await db.Users.AnyAsync(x=>x.Email==email))throw new InvalidOperationException("User already exists");
  var user=new User{Email=email,Role=role,TenantId=tenant};var secret=Totp.CreateSecret();
  user.PasswordHash=UserPasswords.Create().HashPassword(user,password);user.TotpSecret=secrets.Protect(tenant??Guid.Empty,$"totp:{user.Id}",secret);
  await using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
  if(!OperatingSystem.IsWindows())File.SetUnixFileMode(output,UnixFileMode.UserRead|UnixFileMode.UserWrite);
  db.Users.Add(user);db.Audit.Add(new AuditEvent{TenantId=tenant,Actor="local-provisioning",Action="user.created",Resource=user.Id.ToString()});await db.SaveChangesAsync();
  await using var writer=new StreamWriter(file);await writer.WriteAsync($"TOTP secret: {secret}\nProvision in an authenticator app; remove this file afterwards.\n");
 }
 public static async Task RecoverUser(ManagementDb db,ISecretStore secrets,IConfiguration config)
 {
  var email=config["Provision:Email"]?.Trim().ToLowerInvariant()??throw new InvalidOperationException("Provision:Email required");
  var password=File.ReadAllText(config["Provision:PasswordFile"]??throw new InvalidOperationException("Protected password file required")).TrimEnd('\r','\n');
  var output=config["Provision:TotpOutputFile"]??throw new InvalidOperationException("Protected TOTP output file required");
  if(password.Length is <14 or >200)throw new InvalidOperationException("Password must be 14–200 characters");
  await using var tx=await db.Database.BeginTransactionAsync();
  await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email)))})");
  var user=await db.Users.SingleOrDefaultAsync(x=>x.Email==email&&x.Active)??throw new InvalidOperationException("Active user not found");
  if(user.TenantId is {} tenant&&!await db.Customers.AnyAsync(x=>x.TenantId==tenant&&x.Active))throw new InvalidOperationException("Customer is inactive");
  var secret=Totp.CreateSecret();
  user.PasswordHash=UserPasswords.Create().HashPassword(user,password);user.TotpSecret=secrets.Protect(user.TenantId??Guid.Empty,$"totp:{user.Id}",secret);
  user.LastTotpStep=-1;user.FailedLogins=0;user.LockedUntil=null;user.SessionVersion=Guid.NewGuid();
  db.Audit.Add(new AuditEvent{TenantId=user.TenantId,Actor="local-recovery",Action="user.credentials-recovered",Resource=user.Id.ToString()});
  await using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
  if(!OperatingSystem.IsWindows())File.SetUnixFileMode(output,UnixFileMode.UserRead|UnixFileMode.UserWrite);
  try
  {
   var data=System.Text.Encoding.UTF8.GetBytes($"TOTP secret: {secret}\nRegister the replacement in your authenticator; remove this file afterwards.\n");
   await file.WriteAsync(data);file.Flush(true);await db.SaveChangesAsync();await tx.CommitAsync();
  }
  catch{await file.DisposeAsync();File.Delete(output);throw;}
 }

}
