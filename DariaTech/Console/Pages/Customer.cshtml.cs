using DariaTech.Contracts;
using DariaTech.Console.Data;
using DariaTech.Console.Services;
using DariaTech.Console.Security;
using DariaTech.Console.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed class CustomerModel(ManagementDb db,IOptions<MonitoringOptions> options,AgentReleases agents,ISecretStore secrets):PageModel
{
 // One backup of this customer, wherever its device is (like Acronis "Backup storage"): it stays listed and
 // recoverable when the device is offline or replaced.
 public sealed record BackupRow(ManagedJob Job,string DeviceName,bool DeviceActive,DateTimeOffset? LastSeen,string Destination,DateTimeOffset? LastSuccess,Guid? ActionsJobId,string? SourceName);
 public List<BackupRow> Backups {get;private set;}=[];
 public List<DestinationTemplate> Locations {get;private set;}=[];
 public List<Device> ActiveDevices {get;private set;}=[];
 public bool IsAdmin=>User.IsInRole("SuperAdmin")||User.IsInRole("Administrator");
 public bool CanManageLocations=>User.IsInRole("SuperAdmin");
 public string? RecoveryMessage {get;private set;}public bool RecoveryFailed {get;private set;}
 public Customer Customer{get;private set;}=null!;public List<Site> Sites{get;private set;}=[];public List<DeviceRow> Rows{get;private set;}=[];public string? Token{get;private set;}
 public string Platform{get;private set;}="win-x64";public AgentRelease? Release{get;private set;}public AgentAsset? Asset{get;private set;}public string? Command{get;private set;}public bool AllowManaged{get;private set;}=true;
 public string ReleasesPage=>agents.ReleasesPage;
 public bool CanManage=>User.IsInRole("SuperAdmin")||User.IsInRole("Administrator")||User.IsInRole("Technician");
 public async Task<IActionResult> OnGetAsync(Guid id,Guid? recovery=null)
 {
  var c=await db.Customers.SingleOrDefaultAsync(x=>x.Id==id);if(c is null)return NotFound();Customer=c;Sites=await db.Sites.Where(x=>x.TenantId==c.TenantId).ToListAsync();Rows=(await Overview.Load(db,options.Value)).Where(x=>x.Device.TenantId==c.TenantId).ToList();
  await LoadBackups();
  if(recovery is {} r&&Backups.SingleOrDefault(x=>x.Job.Id==r) is {} created)RecoveryMessage=$"„{created.Job.Name}“ wird auf {created.DeviceName} eingerichtet. Sobald das Gerät die Verbindung bestätigt, dort „Wiederherstellen“ öffnen.";
  return Page();
 }
 private async Task LoadBackups()
 {
  var tenant=Customer.TenantId;
  var devices=await db.Devices.Where(x=>x.TenantId==tenant).ToDictionaryAsync(x=>x.Id);
  ActiveDevices=devices.Values.Where(x=>x.Active).OrderBy(x=>x.Name).ToList();
  var jobs=await db.ManagedJobs.Where(x=>x.TenantId==tenant).OrderBy(x=>x.Name).ToListAsync();
  var reported=await db.Jobs.Where(x=>x.TenantId==tenant).ToListAsync();
  var success=await db.Runs.Where(x=>x.TenantId==tenant&&x.Status==RunStatus.Success).GroupBy(x=>x.JobId).Select(g=>new{g.Key,Last=g.Max(x=>x.Started)}).ToDictionaryAsync(x=>x.Key,x=>(DateTimeOffset?)x.Last);
  var revisions=await db.ConfigurationRevisions.Where(x=>x.TenantId==tenant).ToListAsync();
  foreach(var j in jobs)
  {
   var r=revisions.SingleOrDefault(x=>x.ManagedJobId==j.Id&&x.Revision==j.LatestRevision);
   var destination=r is null?"–":DestinationForm.Describe(ConfigurationApi.Read(secrets,r).TargetUrl);
   var backup=reported.FirstOrDefault(x=>x.DeviceId==j.DeviceId&&x.LocalId==j.LocalJobId);
   var device=devices.GetValueOrDefault(j.DeviceId);
   Backups.Add(new(j,device?.Name??"Gelöschtes Gerät",device?.Active==true,device?.LastHeartbeat,destination,backup is null?null:success.GetValueOrDefault(backup.Id),backup?.Id,
    j.SourceManagedJobId is {} sid?jobs.FirstOrDefault(x=>x.Id==sid)?.Name:null));
  }
  Locations=await DestinationTemplates.For(db.DestinationTemplates,tenant).ToListAsync();
 }
 public async Task<IActionResult> OnPostRecoverAsync(Guid id,Guid sourceJobId,Guid targetDeviceId)
 {
  if(!IsAdmin)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(Backups.All(x=>x.Job.Id!=sourceJobId))return NotFound();
  var (job,error)=await ConfigurationApi.CreateRecoveryCopy(sourceJobId,targetDeviceId,db,secrets,HttpContext);
  if(job is null){RecoveryFailed=true;RecoveryMessage=error;return Page();}
  return RedirectToPage(new{id,recovery=job.Id});
 }
 public async Task<IActionResult> OnPostSiteAsync(Guid id,string siteName,string? address)
 {
  if(!CanManage)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(!ManagementApi.Text(siteName,200)||(address?.Length??0)>1000)return BadRequest();
  if(await db.Sites.AnyAsync(x=>x.TenantId==Customer.TenantId&&x.Name==siteName)){ModelState.AddModelError("","Standort existiert bereits.");return Page();}
  var s=new Site{TenantId=Customer.TenantId,Name=siteName,Address=address??""};db.Sites.Add(s);ManagementApi.Audit(db,HttpContext,s.TenantId,"site.created",s.Id);await db.SaveChangesAsync();return RedirectToPage(new{id});
 }
 public async Task<IActionResult> OnPostEnrollAsync(Guid id,Guid siteId,int validMinutes,string? platform,bool allowManaged)
 {
  if(!CanManage)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(!Customer.Active||validMinutes<5||validMinutes>1440||Sites.All(x=>x.Id!=siteId)||!AgentReleases.Platforms.Contains(platform??"win-x64"))return BadRequest();
  Platform=platform??"win-x64";AllowManaged=allowManaged;
  Token=Tokens.Create();var e=new EnrollmentToken{TenantId=Customer.TenantId,SiteId=siteId,Expires=DateTimeOffset.UtcNow.AddMinutes(validMinutes),TokenHash=Tokens.Hash(Token)};db.EnrollmentTokens.Add(e);ManagementApi.Audit(db,HttpContext,e.TenantId,"enrollment.issued",e.Id);await db.SaveChangesAsync();
  // The package comes from the newest published agent release; without one the token still works with a manually downloaded installer.
  Release=await agents.Latest(HttpContext.RequestAborted);Asset=Release?.Assets.FirstOrDefault(x=>x.Platform==Platform);
  Command=AgentReleases.Command(Platform,Asset,$"{Request.Scheme}://{Request.Host}{Request.PathBase}",Token,AllowManaged);
  Response.Headers.CacheControl="no-store";return Page();
 }
}
