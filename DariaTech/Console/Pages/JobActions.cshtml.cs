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
 // Guided restore: 1. version (from the latest loaded version list), 2. files of that version, 3. where to.
 public RestoreCatalog? Versions {get;private set;}
 public DateTimeOffset? Point {get;private set;}
 public RestoreCatalog? Files {get;private set;}public string? FilesFilter {get;private set;}
 public bool Waiting {get;private set;}
 public bool FourEyes {get;private set;}
 public RemoteCommand? LastCheck {get;private set;}
 public string Platform {get;private set;}="";
 public string RestoreRootHint=>Platform switch{"win-x64"=>@"C:\ProgramData\DariaTechBackup\Restores",var p when p.StartsWith("osx")=>"/Library/Application Support/DariaTechBackup/Restores","" =>"Wiederherstellungsordner des Geräts",_=>"/var/lib/dariatech-backup-restores"};
 public string DefaultFolder=>"Wiederherstellung-"+DateTime.Now.ToString("yyyyMMdd-HHmm");
 public static string ActionLabel(RemoteAction a)=>a switch
 {
  RemoteAction.RunBackup=>"Backup starten",RemoteAction.StopBackup=>"Lauf stoppen",RemoteAction.VerifyBackup=>"Backup prüfen",RemoteAction.Restore=>"Wiederherstellung",
  RemoteAction.ListRestorePoints=>"Versionen laden",RemoteAction.ListRestoreFiles=>"Dateien laden",RemoteAction.RestoreSaas=>"Cloud-Wiederherstellung",RemoteAction.RestoreProxmox=>"Proxmox-Wiederherstellung",_=>a.ToString(),
 };
 public static (string Text,string Css) StatusLabel(RemoteCommand c)=>c.Status switch
 {
  "Pending" when c.Expires<DateTimeOffset.UtcNow=>("Abgelaufen – Gerät war nicht erreichbar","offline"),
  "AwaitingApproval" when c.Expires<DateTimeOffset.UtcNow=>("Abgelaufen – nicht freigegeben","offline"),
  "Pending"=>("Wartet auf Gerät","waiting"),"AwaitingApproval"=>("Wartet auf Freigabe","warning"),"Accepted"=>("Läuft","waiting"),
  "Completed"=>("Erfolgreich","healthy"),"Failed"=>("Fehlgeschlagen","critical"),"Rejected"=>("Vom Gerät abgelehnt","critical"),"Indeterminate"=>("Ergebnis unklar","warning"),
  var other=>(other,"offline"),
 };
 public static string ErrorText(string? code)=>code switch
 {
  null or ""=>"",
  "PolicyRejected"=>"Auf dem Gerät nicht freigegeben (Fernaktionen bzw. Wiederherstellungsordner fehlen – Agent mit aktuellem Befehl aus „Gerät hinzufügen“ neu installieren).",
  "EngineTaskFailed"=>"Die Backup-Engine meldet einen Fehler (z. B. Ziel nicht erreichbar oder falsches Passwort).",
  "SourceAccessDenied"=>"Kein Zugriff auf die zu sichernden Ordner. Auf einem Mac braucht der Agent „Festplattenvollzugriff“: Systemeinstellungen → Datenschutz & Sicherheit → Festplattenvollzugriff → „+“ → ⇧⌘G → /Library/Application Support/DariaTechBackup/agent/DariaTech.Agent hinzufügen und einschalten.",
  "RepairNeeded"=>"Im Speicherziel liegen Sicherungen, die dieses Gerät nicht kennt (z. B. nach Neuinstallation). Über „Gerät ersetzen“ auf der Geräteseite werden sie übernommen; sonst bitte ein eigenes, leeres Zielverzeichnis verwenden.",
  "PassphraseInvalid"=>"Die Backup-Passphrase passt nicht zu den Sicherungen im Speicherziel.",
  "TargetFolderMissing"=>"Der Zielordner im Speicherziel existiert nicht. Mit „Verbindung testen“ im Backup-Job kann er angelegt werden.",
  "TargetLoginFailed"=>"Das Speicherziel hat die Anmeldung abgelehnt. Benutzername und Passwort (bei Nextcloud/OpenCloud ein App-Passwort) prüfen.",
  "TargetUnreachable"=>"Das Speicherziel ist vom Gerät aus nicht erreichbar (Adresse, Internet oder Firewall prüfen).",
  "DispatchIndeterminate"=>"Das Gerät konnte den Start nicht bestätigen.",
  var other=>other,
 };
 public async Task<IActionResult> OnGetAsync(Guid id,Guid? catalogId,string? point)
 {
  JobId=id;if(!await Load())return NotFound();
  if(DateTimeOffset.TryParse(point,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var p))Point=p.ToUniversalTime();
  await LoadGuide();
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
 // Quick actions and the guided restore post here; all go through the same signed command path.
 public async Task<IActionResult> OnPostGuideAsync(string step,string? point,string? filter,string[]? paths,string? target,string? folder)
 {
  if(!await Load())return NotFound();
  DateTimeOffset? at=DateTimeOffset.TryParse(point,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var p)?p.ToUniversalTime():null;
  CommandInput? input=step switch
  {
   "run"=>new(JobId,RemoteAction.RunBackup,null,15),"stop"=>new(JobId,RemoteAction.StopBackup,null,15),"verify"=>new(JobId,RemoteAction.VerifyBackup,null,15),
   "versions"=>new(JobId,RemoteAction.ListRestorePoints,null,15),
   "files" when at is not null=>new(JobId,RemoteAction.ListRestoreFiles,null,15,new(at,string.IsNullOrWhiteSpace(filter)?null:filter.Trim())),
   "restore" when at is not null&&paths is {Length:>0}=>new(JobId,RemoteAction.Restore,new(at.Value,paths,target=="original"?"":(folder??"").Trim(),target=="original"),15),
   _=>null,
  };
  if(input is null){Error=step=="restore"?"Bitte mindestens eine Datei oder einen Ordner auswählen.":"Bitte zuerst eine Version wählen.";Point=at;await LoadGuide();return Page();}
  if(input.Restore is {OriginalLocation:false} r&&!CommandProtocol.SafeFolder(r.DestinationFolder)){Error="Der Ordnername darf nur Buchstaben, Ziffern, - und _ enthalten.";Point=at;await LoadGuide();return Page();}
  var result=await CommandApi.Request(input,db,signer,secrets,HttpContext);
  if(result is IStatusCodeHttpResult {StatusCode:>=400} rejected)
  {
   Error=rejected.StatusCode==409&&step=="run"?"Über eine Wiederherstellungs-Verbindung werden keine Backups gestartet."
    :rejected.StatusCode==403?"Für diese Aktion fehlt Ihnen die Berechtigung (Wiederherstellen: Administrator).":"Die Aktion wurde abgelehnt. Bitte Auswahl prüfen.";
   Point=at;await LoadGuide();return Page();
  }
  return RedirectToPage(new{id=JobId,point=at?.ToString("O")});
 }
 private async Task LoadGuide()
 {
  Platform=await db.Agents.Where(x=>x.DeviceId==Device.Id).Select(x=>x.Platform).FirstOrDefaultAsync()??"";
  FourEyes=await CommandApi.SecondApprovalPossible(db);
  var now=DateTimeOffset.UtcNow;
  // Refresh while the device still has to answer; a long-running task stops refreshing after two hours.
  Waiting=Commands.Any(x=>x.Status=="Pending"&&x.Expires>now||x.Status=="Accepted"&&x.Expires>now.AddHours(-2));
  LastCheck=Commands.FirstOrDefault(x=>x.Action==RemoteAction.VerifyBackup);
  var versions=Commands.FirstOrDefault(x=>x.Action==RemoteAction.ListRestorePoints&&x.EncryptedCatalog is not null);
  if(versions is not null)Versions=ReadCatalog(versions);
  if(Point is {} point)
  {
   foreach(var c in Commands.Where(x=>x.Action==RemoteAction.ListRestoreFiles&&x.EncryptedCatalog is not null))
   {
    var request=JsonSerializer.Deserialize<DeviceCommand>(Convert.FromBase64String(secrets.Unprotect(c.TenantId,$"command:{c.Id}",c.EncryptedPayload)))!;
    if(request.Catalog?.Snapshot is {} snapshot&&Math.Abs((snapshot-point).TotalSeconds)<1){Files=ReadCatalog(c);FilesFilter=request.Catalog.Prefix;break;}
   }
  }
 }
 private RestoreCatalog ReadCatalog(RemoteCommand c)=>JsonSerializer.Deserialize<RestoreCatalog>(secrets.Unprotect(c.TenantId,$"catalog:{c.Id}",c.EncryptedCatalog!))!;
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
