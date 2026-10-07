using DariaTech.Console.Data;
using DariaTech.Console.Services;
using DariaTech.Console.Security;
using DariaTech.Console.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class CustomerModel(ManagementDb db,IOptions<MonitoringOptions> options):PageModel
{
 public Customer Customer{get;private set;}=null!;public List<Site> Sites{get;private set;}=[];public List<DeviceRow> Rows{get;private set;}=[];public string? Token{get;private set;}
 public bool CanManage=>User.IsInRole("SuperAdmin")||User.IsInRole("Administrator")||User.IsInRole("Technician");
 public async Task<IActionResult> OnGetAsync(Guid id){var c=await db.Customers.SingleOrDefaultAsync(x=>x.Id==id);if(c is null)return NotFound();Customer=c;Sites=await db.Sites.Where(x=>x.TenantId==c.TenantId).ToListAsync();Rows=(await Overview.Load(db,options.Value)).Where(x=>x.Device.TenantId==c.TenantId).ToList();return Page();}
 public async Task<IActionResult> OnPostSiteAsync(Guid id,string siteName,string? address)
 {
  if(!CanManage)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(!ManagementApi.Text(siteName,200)||(address?.Length??0)>1000)return BadRequest();
  if(await db.Sites.AnyAsync(x=>x.TenantId==Customer.TenantId&&x.Name==siteName)){ModelState.AddModelError("","Standort existiert bereits.");return Page();}
  var s=new Site{TenantId=Customer.TenantId,Name=siteName,Address=address??""};db.Sites.Add(s);ManagementApi.Audit(db,HttpContext,s.TenantId,"site.created",s.Id);await db.SaveChangesAsync();return RedirectToPage(new{id});
 }
 public async Task<IActionResult> OnPostEnrollAsync(Guid id,Guid siteId,int validMinutes)
 {
  if(!CanManage)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(!Customer.Active||validMinutes<5||validMinutes>1440||Sites.All(x=>x.Id!=siteId))return BadRequest();
  Token=Tokens.Create();var e=new EnrollmentToken{TenantId=Customer.TenantId,SiteId=siteId,Expires=DateTimeOffset.UtcNow.AddMinutes(validMinutes),TokenHash=Tokens.Hash(Token)};db.EnrollmentTokens.Add(e);ManagementApi.Audit(db,HttpContext,e.TenantId,"enrollment.issued",e.Id);await db.SaveChangesAsync();Response.Headers.CacheControl="no-store";return Page();
 }
}
