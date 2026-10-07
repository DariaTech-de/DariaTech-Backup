using System.Security.Claims;
using System.Data;
using DariaTech.Contracts;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Api;

public sealed record CustomerInput(string Name,string Number,string Contact,string Email,string Phone,string Address,string Notes,bool Active);
public sealed record SiteInput(Guid TenantId,string Name,string Address);
public sealed record EnrollmentInput(Guid TenantId,Guid SiteId,int ValidMinutes);
public sealed record DeviceInput(string Name,Guid SiteId);
public sealed record UserInput(UserRole Role,Guid? TenantId,bool Active);
public sealed record StorageInput(Guid TenantId,string Name,string Backend,string Credential);
public static class ManagementApi
{
 public static void MapManagementApi(this WebApplication app)
 {
  var g=app.MapGroup("/api/v1/management").RequireAuthorization();
  g.MapGet("/csrf",(HttpContext ctx,IAntiforgery af)=>Results.Ok(new{token=af.GetAndStoreTokens(ctx).RequestToken}));
  g.MapGet("/customers",async(ManagementDb db,TenantScope scope)=>await db.Customers.OrderBy(x=>x.Name).Select(x=>new{x.Id,x.TenantId,x.Name,x.Number,x.Contact,x.Email,x.Phone,x.Address,x.Active,Notes=scope.Global?x.Notes:""}).ToListAsync());
  g.MapGet("/sites",async(ManagementDb db)=>await db.Sites.OrderBy(x=>x.Name).ToListAsync());
  g.MapGet("/devices",async(ManagementDb db)=>await db.Devices.OrderBy(x=>x.Name).ToListAsync());
  g.MapGet("/devices/{id:guid}",async(Guid id,ManagementDb db)=>
  {
   var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id);
   if(device is null)return Results.NotFound();
   var jobs=await db.Jobs.Where(x=>x.DeviceId==id).ToListAsync();var ids=jobs.Select(x=>x.Id).ToArray();
   return Results.Ok(new{device,jobs,runs=await db.Runs.Where(x=>ids.Contains(x.JobId)).OrderByDescending(x=>x.Started).Take(200).ToListAsync(),alerts=await db.Alerts.Where(x=>x.DeviceId==id).ToListAsync(),agent=await db.Agents.Where(x=>x.DeviceId==id).Select(x=>new{x.Version,x.Registered,x.Revoked}).SingleOrDefaultAsync()});
  });
  g.MapPost("/customers",async(CustomerInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(!Text(input.Name,200)||!Text(input.Number,80)||new[]{input.Contact,input.Email,input.Phone,input.Address,input.Notes}.Any(x=>x is null||x.Length>1000))return Results.BadRequest();
   var tenant=new Tenant{Name=input.Name};db.Tenants.Add(tenant);
   var customer=new Customer{TenantId=tenant.Id,Name=input.Name,Number=input.Number,Contact=input.Contact,Email=input.Email,Phone=input.Phone,Address=input.Address,Notes=input.Notes,Active=input.Active};
   db.Customers.Add(customer);Audit(db,ctx,tenant.Id,"customer.created",customer.Id);await db.SaveChangesAsync();
   return Results.Created($"/api/v1/management/customers/{customer.Id}",customer);
  }).RequireAuthorization("Admin");
  g.MapPut("/customers/{id:guid}",async(Guid id,CustomerInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(!Text(input.Name,200)||!Text(input.Number,80)||new[]{input.Contact,input.Email,input.Phone,input.Address,input.Notes}.Any(x=>x is null||x.Length>1000))return Results.BadRequest();
   var c=await db.Customers.SingleOrDefaultAsync(x=>x.Id==id);if(c is null)return Results.NotFound();
   c.Name=input.Name;c.Number=input.Number;c.Contact=input.Contact;c.Email=input.Email;c.Phone=input.Phone;c.Address=input.Address;c.Notes=input.Notes;c.Active=input.Active;
   var tenant=await db.Tenants.SingleAsync(x=>x.Id==c.TenantId);tenant.Name=c.Name;
   Audit(db,ctx,c.TenantId,"customer.updated",c.Id);await db.SaveChangesAsync();return Results.Ok(c);
  }).RequireAuthorization("Admin");
  g.MapPost("/sites",async(SiteInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(!Text(input.Name,200)||input.Address is null||input.Address.Length>1000)return Results.BadRequest();
   if(!await db.Customers.AnyAsync(x=>x.TenantId==input.TenantId && x.Active))return Results.NotFound();
   var s=new Site{TenantId=input.TenantId,Name=input.Name,Address=input.Address};db.Sites.Add(s);Audit(db,ctx,s.TenantId,"site.created",s.Id);await db.SaveChangesAsync();return Results.Ok(s);
  }).RequireAuthorization("Operator");
  g.MapPost("/enrollment-tokens",async(EnrollmentInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(input.ValidMinutes<5||input.ValidMinutes>1440)return Results.BadRequest();
   if(!await db.Sites.AnyAsync(x=>x.Id==input.SiteId&&x.TenantId==input.TenantId)||!await db.Customers.AnyAsync(x=>x.TenantId==input.TenantId&&x.Active))return Results.NotFound();
   var token=Tokens.Create();var e=new EnrollmentToken{TenantId=input.TenantId,SiteId=input.SiteId,TokenHash=Tokens.Hash(token),Expires=DateTimeOffset.UtcNow.AddMinutes(input.ValidMinutes)};
   db.EnrollmentTokens.Add(e);Audit(db,ctx,e.TenantId,"enrollment.issued",e.Id);await db.SaveChangesAsync();ctx.Response.Headers.CacheControl="no-store";return Results.Ok(new{token,e.Expires});
  }).RequireAuthorization("Operator");
  g.MapDelete("/devices/{id:guid}",async(Guid id,ManagementDb db,HttpContext ctx)=>
  {
   var d=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id);if(d is null)return Results.NotFound();d.Active=false;
   var a=await db.Agents.SingleAsync(x=>x.DeviceId==id);a.Revoked=true;
   Audit(db,ctx,d.TenantId,"device.revoked",d.Id);await db.SaveChangesAsync();return Results.NoContent();
  }).RequireAuthorization("Admin");
  g.MapDelete("/customers/{id:guid}",async(Guid id,ManagementDb db,HttpContext ctx)=>
  {
   var customer=await db.Customers.SingleOrDefaultAsync(x=>x.Id==id);if(customer is null)return Results.NotFound();
   customer.Active=false;foreach(var d in await db.Devices.Where(x=>x.TenantId==customer.TenantId).ToListAsync())d.Active=false;
   foreach(var agent in await db.Agents.Where(x=>x.TenantId==customer.TenantId).ToListAsync())agent.Revoked=true;
   foreach(var u in await db.Users.Where(x=>x.TenantId==customer.TenantId).ToListAsync()){u.Active=false;u.SessionVersion=Guid.NewGuid();}
   Audit(db,ctx,customer.TenantId,"customer.archived",customer.Id);await db.SaveChangesAsync();return Results.NoContent();
  }).RequireAuthorization("Admin");
  g.MapPut("/sites/{id:guid}",async(Guid id,SiteInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(!Text(input.Name,200)||input.Address is null||input.Address.Length>1000)return Results.BadRequest();
   var site=await db.Sites.SingleOrDefaultAsync(x=>x.Id==id&&x.TenantId==input.TenantId);if(site is null)return Results.NotFound();
   site.Name=input.Name;site.Address=input.Address;Audit(db,ctx,site.TenantId,"site.updated",site.Id);await db.SaveChangesAsync();return Results.Ok(site);
  }).RequireAuthorization("Operator");
  g.MapDelete("/sites/{id:guid}",async(Guid id,ManagementDb db,HttpContext ctx)=>
  {
   var site=await db.Sites.SingleOrDefaultAsync(x=>x.Id==id);if(site is null)return Results.NotFound();
   if(await db.Devices.AnyAsync(x=>x.SiteId==id))return Results.Conflict(new{message="Site has device history and cannot be deleted"});
   db.Sites.Remove(site);Audit(db,ctx,site.TenantId,"site.deleted",site.Id);await db.SaveChangesAsync();return Results.NoContent();
  }).RequireAuthorization("Admin");
  g.MapPut("/devices/{id:guid}",async(Guid id,DeviceInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(!Text(input.Name,200))return Results.BadRequest();var device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id);if(device is null)return Results.NotFound();
   if(!await db.Sites.AnyAsync(x=>x.Id==input.SiteId&&x.TenantId==device.TenantId))return Results.NotFound();
   device.Name=input.Name;device.SiteId=input.SiteId;Audit(db,ctx,device.TenantId,"device.updated",device.Id);await db.SaveChangesAsync();return Results.Ok(device);
  }).RequireAuthorization("Operator");
  g.MapGet("/users",async(ManagementDb db)=>await db.Users.Select(x=>new{x.Id,x.Email,x.TenantId,x.Role,x.Active}).ToListAsync()).RequireAuthorization("Admin");
  g.MapPut("/users/{id:guid}",async(Guid id,UserInput input,ManagementDb db,HttpContext ctx)=>
  {
   if(!Enum.IsDefined(input.Role)||(input.Role is UserRole.CustomerAdmin or UserRole.CustomerReadOnly)!=(input.TenantId is not null))return Results.BadRequest();
   if(input.TenantId is {} tenant&&!await db.Customers.AnyAsync(x=>x.TenantId==tenant&&x.Active))return Results.NotFound();
   await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({73941002L})");
   var user=await db.Users.SingleOrDefaultAsync(x=>x.Id==id);if(user is null)return Results.NotFound();
   if(user.Role==UserRole.SuperAdmin&&user.Active&&(!input.Active||input.Role!=UserRole.SuperAdmin)&&await db.Users.CountAsync(x=>x.Role==UserRole.SuperAdmin&&x.Active)<=1)return Results.Conflict(new{message="At least one active SuperAdmin is required"});
   // A TOTP secret is purpose/tenant bound; rewrap it when moving a customer identity.
   var secrets=ctx.RequestServices.GetRequiredService<ISecretStore>();var totp=secrets.Unprotect(user.TenantId??Guid.Empty,$"totp:{user.Id}",user.TotpSecret);
   user.Role=input.Role;user.TenantId=input.TenantId;user.Active=input.Active;user.SessionVersion=Guid.NewGuid();user.TotpSecret=secrets.Protect(user.TenantId??Guid.Empty,$"totp:{user.Id}",totp);
   Audit(db,ctx,user.TenantId,"user.permissions-changed",user.Id);await db.SaveChangesAsync();await tx.CommitAsync();return Results.NoContent();
  }).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
  g.MapGet("/alerts",async(ManagementDb db)=>await db.Alerts.Where(x=>x.Resolved==null).OrderBy(x=>x.Severity).ToListAsync());
  g.MapGet("/audit",async(ManagementDb db)=>await db.Audit.OrderByDescending(x=>x.Id).Take(500).ToListAsync());
  g.MapGet("/export",async(ManagementDb db,HttpContext ctx,TenantScope scope)=>
  {
   var tenants=await db.Customers.Select(x=>x.TenantId).ToArrayAsync();Audit(db,ctx,tenants.Length==1?tenants[0]:null,"data.exported",Guid.Empty);await db.SaveChangesAsync();
   return Results.Ok(new{customers=await db.Customers.Select(x=>new{x.Id,x.TenantId,x.Name,x.Number,x.Contact,x.Email,x.Phone,x.Address,x.Active,Notes=scope.Global?x.Notes:""}).ToListAsync(),sites=await db.Sites.ToListAsync(),devices=await db.Devices.ToListAsync(),jobs=await db.Jobs.ToListAsync(),runs=await db.Runs.OrderByDescending(x=>x.Started).Take(10000).ToListAsync(),alerts=await db.Alerts.ToListAsync(),truncatedRunExport=await db.Runs.CountAsync()>10000});
  });
  g.MapPost("/storage-targets",async(StorageInput input,ManagementDb db,ISecretStore secrets,HttpContext ctx)=>
  {
   if(!Text(input.Name,200)||!new[]{"s3","sftp","webdav","file"}.Contains(input.Backend)||!Text(input.Credential,2000))return Results.BadRequest();
   if(!await db.Customers.AnyAsync(x=>x.TenantId==input.TenantId))return Results.NotFound();
   var s=new StorageTarget{TenantId=input.TenantId,Name=input.Name,Backend=input.Backend};s.EncryptedCredential=secrets.Protect(s.TenantId,$"storage:{s.Id}",input.Credential);db.StorageTargets.Add(s);
   Audit(db,ctx,s.TenantId,"storage.created",s.Id);await db.SaveChangesAsync();return Results.Ok(new{s.Id,s.Name,s.Backend});
  }).RequireAuthorization("Admin");
  g.MapGet("/storage-targets",async(ManagementDb db)=>await db.StorageTargets.Select(x=>new{x.Id,x.TenantId,x.Name,x.Backend}).ToListAsync());
 }
 public static bool Text(string? s,int max)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=max&&!s.Any(char.IsControl);
 public static void Audit(ManagementDb db,HttpContext ctx,Guid? tenant,string action,Guid resource)=>db.Audit.Add(new AuditEvent{TenantId=tenant,Actor=ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)??"anonymous",Action=action,Resource=resource.ToString()});
}
