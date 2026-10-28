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
public sealed class CustomerModel(ManagementDb db,IOptions<MonitoringOptions> options,AgentReleases agents,ISecretStore secrets,CommandSigning signer):PageModel
{
 // One backup of this customer, wherever its device is (like Acronis "Backup storage"): it stays listed and
 // recoverable when the device is offline or replaced.
 public sealed record BackupRow(ManagedJob Job,string DeviceName,bool DeviceActive,DateTimeOffset? LastSeen,string Destination,DateTimeOffset? LastSuccess,Guid? ActionsJobId,string? SourceName,bool DeviceDeleted);
 public List<BackupRow> Backups {get;private set;}=[];
 // Deleted backups no longer run but their versions stay restorable; removed recovery copies are not listed at all.
 public List<BackupRow> DeletedBackups {get;private set;}=[];
 public sealed record DeletedDevice(Device Device,string StopState,string? AgentVersion,bool SelfStopping);
 public List<DeletedDevice> DeletedDevices {get;private set;}=[];
 public string? Notice {get;private set;}
 public CustomerSetup Setup {get;private set;}=null!;
 public List<EnrollmentToken> Tokens {get;private set;}=[];
 public List<(string Token,string? Command)> Issued {get;private set;}=[];
 public List<DestinationTemplate> Locations {get;private set;}=[];
 public List<Device> ActiveDevices {get;private set;}=[];
 public bool IsAdmin=>User.IsInRole("SuperAdmin")||User.IsInRole("Administrator");
 public bool CanManageLocations=>User.IsInRole("SuperAdmin");
 public string? RecoveryMessage {get;private set;}public bool RecoveryFailed {get;private set;}
 public Customer Customer{get;private set;}=null!;public List<Site> Sites{get;private set;}=[];public List<DeviceRow> Rows{get;private set;}=[];public string? Token{get;private set;}
 public string Platform{get;private set;}="win-x64";public AgentRelease? Release{get;private set;}public AgentAsset? Asset{get;private set;}public string? Command{get;private set;}public bool AllowManaged{get;private set;}=true;public bool AllowRemote{get;private set;}=true;
 public string ReleasesPage=>agents.ReleasesPage;
 public bool CanManage=>User.IsInRole("SuperAdmin")||User.IsInRole("Administrator")||User.IsInRole("Technician");
 public async Task<IActionResult> OnGetAsync(Guid id,Guid? recovery=null,Guid? deletedDevice=null,Guid? deletedBackup=null)
 {
  var c=await db.Customers.SingleOrDefaultAsync(x=>x.Id==id);if(c is null)return NotFound();Customer=c;Sites=await db.Sites.Where(x=>x.TenantId==c.TenantId).ToListAsync();Rows=(await Overview.Load(db,options.Value)).Where(x=>x.Device.TenantId==c.TenantId).ToList();
  await LoadBackups();Setup=await Onboarding.Load(db,c,options.Value);
  Tokens=await db.EnrollmentTokens.Where(x=>x.TenantId==c.TenantId).OrderByDescending(x=>x.Created).Take(20).ToListAsync();
  Stops=await ContractEnd.Devices(db,c);
  if(recovery is {} r&&Backups.SingleOrDefault(x=>x.Job.Id==r) is {} created)RecoveryMessage=$"„{created.Job.Name}“ wird auf {created.DeviceName} eingerichtet. Sobald das Gerät die Verbindung bestätigt, dort „Wiederherstellen“ öffnen.";
  if(deletedDevice is {} dd&&DeletedDevices.FirstOrDefault(x=>x.Device.Id==dd) is {} gone)Notice=$"„{gone.Device.Name}“ wurde gelöscht und erscheint nicht mehr in Listen und Überwachung. Ist das Gerät noch eingeschaltet, hält es beim nächsten Kontakt seine Sicherungen an. Seine Backups bleiben im Speicherziel und lassen sich unter „Backups & Wiederherstellung“ zurückholen.";
  if(deletedBackup is {} bid&&await db.ManagedJobs.SingleOrDefaultAsync(x=>x.Id==bid&&x.TenantId==c.TenantId&&x.Deleted!=null) is {} deletedJob)
   RecoveryMessage=deletedJob.RecoveryCopy?$"Wiederherstellungs-Verbindung „{deletedJob.Name}“ entfernt.":$"„{deletedJob.Name}“ wurde gelöscht. Es sichert ab dem nächsten Kontakt des Geräts nicht mehr; die Versionen im Speicherziel bleiben erhalten und lassen sich unter „Gelöschte Backups“ wiederherstellen.";
  return Page();
 }
 private async Task LoadBackups()
 {
  var tenant=Customer.TenantId;
  var devices=await db.Devices.Where(x=>x.TenantId==tenant).ToDictionaryAsync(x=>x.Id);
  ActiveDevices=devices.Values.Where(x=>x.Active).OrderBy(x=>x.Name).ToList();
  var removed=devices.Values.Where(x=>x.Deleted!=null).OrderByDescending(x=>x.Deleted).ToList();var removedIds=removed.Select(x=>x.Id).ToList();
  var versions=await db.Agents.Where(x=>removedIds.Contains(x.DeviceId)).OrderByDescending(x=>x.Registered).ToListAsync();
  DeletedDevices=removed.Select(d=>{var v=versions.FirstOrDefault(x=>x.DeviceId==d.Id)?.Version;return new DeletedDevice(d,ContractEnd.StopState(d),v,ConfigurationApi.RetirementCapable(v));}).ToList();
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
   var row=new BackupRow(j,device?.Name??"Gelöschtes Gerät",device?.Active==true,device?.LastHeartbeat,destination,backup is null?null:success.GetValueOrDefault(backup.Id),backup?.Id,
    j.SourceManagedJobId is {} sid?jobs.FirstOrDefault(x=>x.Id==sid)?.Name:null,device is null||device.Deleted is not null);
   if(j.Deleted is null)Backups.Add(row);else if(!j.RecoveryCopy)DeletedBackups.Add(row);
  }
  Locations=await DestinationTemplates.For(db.DestinationTemplates,tenant).ToListAsync();
 }
 public async Task<IActionResult> OnPostRecoverAsync(Guid id,Guid sourceJobId,Guid targetDeviceId)
 {
  if(!IsAdmin)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(Backups.Concat(DeletedBackups).All(x=>x.Job.Id!=sourceJobId))return NotFound();
  var (job,error)=await ConfigurationApi.CreateRecoveryCopy(sourceJobId,targetDeviceId,db,secrets,HttpContext);
  if(job is null){RecoveryFailed=true;RecoveryMessage=error;return Page();}
  return RedirectToPage(new{id,recovery=job.Id});
 }
 // Also posted from the backup's own pages (job actions, configuration).
 public async Task<IActionResult> OnPostDeleteBackupAsync(Guid id,Guid jobId,bool confirm)
 {
  if(!IsAdmin)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(Backups.All(x=>x.Job.Id!=jobId))return NotFound();
  if(!confirm){RecoveryFailed=true;RecoveryMessage="Bitte bestätigen, dass das Backup gelöscht werden soll.";return Page();}
  if(await ConfigurationApi.DeleteBackup(jobId,db,secrets,HttpContext) is {} error){RecoveryFailed=true;RecoveryMessage=error;return Page();}
  return RedirectToPage(null,null,new{id,deletedBackup=jobId},"backups");
 }
 public async Task<IActionResult> OnPostSiteAsync(Guid id,string siteName,string? address)
 {
  if(!CanManage)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(!ManagementApi.Text(siteName,200)||(address?.Length??0)>1000)return BadRequest();
  if(await db.Sites.AnyAsync(x=>x.TenantId==Customer.TenantId&&x.Name==siteName)){ModelState.AddModelError("","Standort existiert bereits.");return Page();}
  var s=new Site{TenantId=Customer.TenantId,Name=siteName,Address=address??""};db.Sites.Add(s);ManagementApi.Audit(db,HttpContext,s.TenantId,"site.created",s.Id);await db.SaveChangesAsync();return RedirectToPage(new{id});
 }
 public async Task<IActionResult> OnPostEnrollAsync(Guid id,Guid siteId,int validMinutes,string? platform,bool allowManaged,bool allowRemote,int count=1)
 {
  if(!CanManage)return Forbid();if(await OnGetAsync(id) is NotFoundResult)return NotFound();
  if(!Customer.Active||validMinutes<5||validMinutes>1440||count is <1 or >25||Sites.All(x=>x.Id!=siteId)||!AgentReleases.Platforms.Contains(platform??"win-x64"))return BadRequest();
  Platform=platform??"win-x64";AllowManaged=allowManaged;AllowRemote=allowRemote;
  // One single-use token per device: several devices get several tokens, each with its own command.
  Release=await agents.Latest(HttpContext.RequestAborted);Asset=Release?.Assets.FirstOrDefault(x=>x.Platform==Platform);
  for(var i=0;i<count;i++)
  {
   var token=DariaTech.Console.Security.Tokens.Create();var e=new EnrollmentToken{TenantId=Customer.TenantId,SiteId=siteId,Expires=DateTimeOffset.UtcNow.AddMinutes(validMinutes),TokenHash=DariaTech.Console.Security.Tokens.Hash(token)};db.EnrollmentTokens.Add(e);ManagementApi.Audit(db,HttpContext,e.TenantId,"enrollment.issued",e.Id);
   // The package comes from the newest published agent release; without one the token still works with a manually downloaded installer.
   Issued.Add((token,AgentReleases.Command(Platform,Asset,$"{Request.Scheme}://{Request.Host}{Request.PathBase}",token,AllowManaged,AllowRemote?signer.PublicKeyPem:null)));
  }
  await db.SaveChangesAsync();Token=Issued[0].Token;Command=Issued[0].Command;
  Tokens=await db.EnrollmentTokens.Where(x=>x.TenantId==Customer.TenantId).OrderByDescending(x=>x.Created).Take(20).ToListAsync();Setup=await Onboarding.Load(db,Customer,options.Value);
  Response.Headers.CacheControl="no-store";return Page();
 }
 public List<ContractEnd.DeviceStop> Stops {get;private set;}=[];
 public async Task<IActionResult> OnPostContractAsync(Guid id,string step,DateOnly? contractEnd,DateOnly? retainUntil,string? handoverNote,bool confirm)
 {
  if(!IsAdmin)return Forbid();var c=await db.Customers.SingleOrDefaultAsync(x=>x.Id==id);if(c is null)return NotFound();
  if(!confirm&&step!="dates"){ContractMessage="Bitte den Schritt ausdrücklich bestätigen.";ContractFailed=true;}
  else if(await ContractEnd.Run(step,c,db,HttpContext,contractEnd,retainUntil,handoverNote) is {} error){ContractMessage=error;ContractFailed=true;}
  if(ContractFailed){await OnGetAsync(id);return Page();}
  return RedirectToPage(null,null,new{id},"contract");
 }
 public string? ContractMessage {get;private set;}public bool ContractFailed {get;private set;}
 public async Task<IActionResult> OnPostExpectedAsync(Guid id,int? expectedDevices)
 {
  if(!CanManage)return Forbid();var c=await db.Customers.SingleOrDefaultAsync(x=>x.Id==id);if(c is null)return NotFound();
  if(expectedDevices is <0 or >10000)return BadRequest();
  c.ExpectedDevices=expectedDevices;ManagementApi.Audit(db,HttpContext,c.TenantId,"customer.expected-devices",c.Id);await db.SaveChangesAsync();return RedirectToPage(new{id});
 }
}
