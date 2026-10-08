using System.Security.Claims;
using System.Text.Json;
using DariaTech.Contracts;
using DariaTech.Console.Api;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;

[Authorize(Policy="Operator")]
public sealed class JobActionsModel(ManagementDb db,CommandSigning signer,ISecretStore secrets):PageModel
{
 [BindProperty]public Guid JobId {get;set;}
 [BindProperty]public RemoteAction Action {get;set;}
 [BindProperty]public DateTimeOffset Snapshot {get;set;}=DateTimeOffset.UtcNow;
 [BindProperty]public string? Paths {get;set;}="";
 [BindProperty]public string? DestinationFolder {get;set;}="";
 [BindProperty]public string? ProviderTarget {get;set;}="";
 [BindProperty]public bool ConfirmProviderWrites {get;set;}
 [BindProperty]public int TargetGuestId {get;set;}
 [BindProperty]public string? ProxmoxStorage {get;set;}
 [BindProperty]public bool ConfirmGuestImport {get;set;}
 public bool Proxmox {get;private set;}
 [BindProperty]public string? Prefix {get;set;}
 public BackupJob Job {get;private set;}=null!;
 public Device Device {get;private set;}=null!;
 public ManagedJob? Managed {get;private set;}
 public SaasProvider? Provider {get;private set;}
 public string? DirectoryTenant {get;private set;}
 public List<RemoteCommand> Commands {get;private set;}=[];
 public RestoreCatalog? Catalog {get;private set;}
 public bool SigningEnabled=>signer.Enabled;
 public bool Administrator=>User.IsInRole("SuperAdmin")||User.IsInRole("Administrator");
 public string Actor=>User.FindFirstValue(ClaimTypes.NameIdentifier)??"";
 public string? Error {get;private set;}
 public async Task<IActionResult> OnGetAsync(Guid id,Guid? catalogId)
 {
  JobId=id;if(!await Load())return NotFound();
  if(catalogId is {} selected)
  {
   var command=await db.Commands.SingleOrDefaultAsync(x=>x.Id==selected&&x.JobId==JobId);
   if(command?.EncryptedCatalog is {} encrypted)
   {
    Catalog=JsonSerializer.Deserialize<RestoreCatalog>(secrets.Unprotect(command.TenantId,$"catalog:{selected}",encrypted));
    ManagementApi.Audit(db,HttpContext,command.TenantId,"restore.catalog-viewed",selected);await db.SaveChangesAsync();
   }
  }
  return Page();
 }
 public async Task<IActionResult> OnPostRequestAsync()
 {
  if(!await Load())return NotFound();
  var paths=(Paths??"").Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
  RestoreSelection? file=Action==RemoteAction.Restore?new(Snapshot.ToUniversalTime(),paths,DestinationFolder??""):null;
  SaasRestoreSelection? cloud=Action==RemoteAction.RestoreSaas?new(Managed?.AppliedRevision??0,Snapshot.ToUniversalTime(),paths,ProviderTarget??"",ConfirmProviderWrites):null;
  ProxmoxRestoreSelection? guest=Action==RemoteAction.RestoreProxmox?new(Managed?.AppliedRevision??0,Snapshot.ToUniversalTime(),paths.Length==1?paths[0]:"",TargetGuestId,ProxmoxStorage??"",ConfirmGuestImport):null;
  CatalogRequest? catalog=Action==RemoteAction.ListRestoreFiles?new(Snapshot.ToUniversalTime(),string.IsNullOrWhiteSpace(Prefix)?null:Prefix):null;
  if(!ModelState.IsValid){Error="Bitte Eingaben prüfen.";return Page();}
  var result=await CommandApi.Request(new(JobId,Action,file,15,catalog,cloud,guest),db,signer,secrets,HttpContext);
  if(result is IStatusCodeHttpResult {StatusCode:>=400}){Error="Die Aktion wurde abgelehnt. Berechtigungen, Signierung, Auswahl und angewendete Konfigurationsrevision prüfen.";return Page();}
  return RedirectToPage(new{id=JobId});
 }
 public async Task<IActionResult> OnPostApproveAsync(Guid commandId)
 {
  if(!await Load()||!await db.Commands.AnyAsync(x=>x.Id==commandId&&x.JobId==JobId))return NotFound();
  var result=await CommandApi.Approve(commandId,db,HttpContext);
  if(result is IStatusCodeHttpResult {StatusCode:>=400}){Error="Freigabe abgelehnt. Ein anderer Administrator muss den noch gültigen Restore bestätigen.";return Page();}
  return RedirectToPage(new{id=JobId});
 }
 private async Task<bool> Load()
 {
  Job=await db.Jobs.SingleOrDefaultAsync(x=>x.Id==JobId&&x.Active)??null!;if(Job is null)return false;
  Device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==Job.DeviceId&&x.Active)??null!;if(Device is null)return false;
  Managed=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.DeviceId==Job.DeviceId&&x.LocalJobId==Job.LocalId);
  if(Managed is {} managed)
  {
   var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==managed.Id&&x.Revision==managed.LatestRevision);
   var definition=ConfigurationApi.Read(secrets,revision);Proxmox=definition.Proxmox is not null;
   var source=definition.Saas;Provider=source?.Provider;DirectoryTenant=source?.DirectoryTenant;
  }
  Commands=await db.Commands.Where(x=>x.JobId==JobId).OrderByDescending(x=>x.Expires).Take(50).ToListAsync();
  return true;
 }
}
