using DariaTech.Contracts;
using DariaTech.Console.Api;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using DariaTech.Console.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;

[Authorize(Policy="Admin")]
public sealed class BackupEditModel(ManagementDb db,ISecretStore secrets):PageModel
{
 [BindProperty]public Guid DeviceId {get;set;}
 [BindProperty]public Guid? ManagedJobId {get;set;}
 [BindProperty]public long Revision {get;set;}
 [BindProperty]public string Name {get;set;}="";
 [BindProperty]public string Provider {get;set;}="Files";
 [BindProperty]public string? SourceMounts {get;set;}
 [BindProperty]public string? ProxmoxGuestIds {get;set;}
 [BindProperty]public string? Sources {get;set;}="";
 [BindProperty]public string? TargetUrl {get;set;}="";
 [BindProperty]public string? Passphrase {get;set;}
 [BindProperty]public int KeepVersions {get;set;}=30;
 [BindProperty]public int RepeatHours {get;set;}=24;
 [BindProperty]public DayOfWeek[] Days {get;set;}=Enum.GetValues<DayOfWeek>();
 [BindProperty]public DateTimeOffset? FirstRun {get;set;}
 [BindProperty]public string? DirectoryTenant {get;set;}="";
 [BindProperty]public string? ClientId {get;set;}="";
 [BindProperty]public string? ClientSecret {get;set;}
 [BindProperty]public string? RefreshToken {get;set;}
 [BindProperty]public string? ServiceAccountJson {get;set;}
 [BindProperty]public string? AdminEmail {get;set;}="";
 [BindProperty]public string[] RootTypes {get;set;}=[];
 [BindProperty]public string[] UserTypes {get;set;}=[];
 [BindProperty]public string? AuthUsername {get;set;}="";
 [BindProperty]public string? AuthPassword {get;set;}
 [BindProperty]public string? AwsAccessKeyId {get;set;}="";
 [BindProperty]public string? AwsSecretKey {get;set;}
 [BindProperty]public string? S3Server {get;set;}="";
 [BindProperty]public string? S3Region {get;set;}="";
 [BindProperty]public string? SshFingerprint {get;set;}="";
 [BindProperty]public string? Excludes {get;set;}="";
 [BindProperty]public string? DestinationKey {get;set;}
 [BindProperty]public Guid? TemplateId {get;set;}
 [BindProperty]public string? DestinationMode {get;set;}="custom";
 public List<DestinationTemplate> Templates {get;private set;}=[];
 public DestinationForm.Parts Destination {get;private set;}=new("file","","","");
 public Dictionary<string,string> OptionValues {get;private set;}=new(StringComparer.OrdinalIgnoreCase);
 public HashSet<string> StoredSecrets {get;private set;}=new(StringComparer.OrdinalIgnoreCase);
 public Device Device {get;private set;}=null!;
 public string? Error {get;private set;}
 public async Task<IActionResult> OnGetAsync(Guid deviceId,Guid? id,string provider="Files")
 {
  DeviceId=deviceId;ManagedJobId=id;
  if(await LoadPrevious() is {} previous)
  {
   Name=previous.Name;Sources=string.Join(Environment.NewLine,previous.Sources);TargetUrl=previous.TargetUrl;KeepVersions=previous.KeepVersions;
   FirstRun=previous.Schedule?.Start;Days=previous.Schedule?.Days??Enum.GetValues<DayOfWeek>();RepeatHours=previous.Schedule?.RepeatHours??24;
   Provider=previous.Proxmox is not null?"Proxmox":previous.SourceMounts is not null?"NAS":previous.Saas?.Provider.ToString()??"Files";
   ProxmoxGuestIds=previous.Proxmox is {} guests?string.Join(',',guests.GuestIds):null;
   SourceMounts=previous.SourceMounts is {} mounts?string.Join(Environment.NewLine,mounts.Select(m=>m.MountPoint+"|"+m.FileSystemType+"|"+m.RemoteSource)):null;DirectoryTenant=previous.Saas?.DirectoryTenant??"";
   RootTypes=previous.Saas?.RootTypes??[];UserTypes=previous.Saas?.UserTypes??[];
   ClientId=previous.Saas?.Credentials.GetValueOrDefault(Provider=="Microsoft365"?"office365-client-id":"google-client-id")??"";
   AdminEmail=previous.Saas?.Credentials.GetValueOrDefault("google-admin-email")??"";
   AuthUsername=previous.BackendOptions.GetValueOrDefault("auth-username")??"";AwsAccessKeyId=previous.BackendOptions.GetValueOrDefault("aws-access-key-id")??"";
   S3Server=previous.BackendOptions.GetValueOrDefault("s3-server-name")??"";S3Region=previous.BackendOptions.GetValueOrDefault("s3-region")??"";SshFingerprint=previous.BackendOptions.GetValueOrDefault("ssh-fingerprint")??"";
   Excludes=string.Join(Environment.NewLine,previous.Filters.Where(x=>!x.Include).Select(x=>x.Expression));
   ShowDestination(previous);
  }
  else if(id is not null)return NotFound();
  else
  {
   Templates=await db.DestinationTemplates.OrderBy(x=>x.Name).ToListAsync();
   if((Templates.FirstOrDefault(x=>x.IsDefault)??Templates.FirstOrDefault()) is {} preferred){TemplateId=preferred.Id;DestinationMode="template";}
   Provider=provider;RootTypes=provider=="Microsoft365"?["Users","Groups","Sites"]:["Users","SharedDrives"];
   UserTypes=provider=="Microsoft365"?["Mailbox","Calendar","Contacts"]:["Gmail","Drive","Calendar"];
  }
  Device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==DeviceId&&x.Active)??null!;
  return Device is null?NotFound():Page();
 }
 public async Task<IActionResult> OnPostAsync()
 {
  var previous=await LoadPrevious();
  if(ManagedJobId is not null&&previous is null)return NotFound();
  Device=await db.Devices.SingleOrDefaultAsync(x=>x.Id==DeviceId&&x.Active)??null!;if(Device is null)return NotFound();
  var validBinding=ModelState.IsValid;
  Dictionary<string,string> storage;
  Templates=await db.DestinationTemplates.OrderBy(x=>x.Name).ToListAsync();
  if(previous is null&&DestinationMode=="template")
  {
   // Saved destination: copy URL and options, then give this job its own folder below it.
   var template=Templates.SingleOrDefault(x=>x.Id==TemplateId);
   var customer=await db.Customers.Where(x=>x.TenantId==Device.TenantId).Select(x=>x.Number).SingleOrDefaultAsync()??"kunde";
   storage=template is null?new():DestinationTemplates.Options(secrets,template);
   TargetUrl=template is null?"":DestinationForm.Below(template.TargetUrl,customer,Device.Name,Name+"-"+Guid.NewGuid().ToString("N")[..6])??"";
   if(template is null||TargetUrl.Length==0)validBinding=false;
   DestinationKey=DestinationForm.Parse(TargetUrl).Key;
  }
  else if(!string.IsNullOrEmpty(DestinationKey))
  {
   // Destination picker: the URL and options come from the selected engine destination only.
   if(previous is not null)DestinationKey=DestinationForm.Parse(previous.TargetUrl).Key; // an existing backup chain keeps its destination
   var (url,options)=DestinationForm.Read(Request.Form,DestinationKey,previous?.BackendOptions);
   storage=options;TargetUrl=previous?.TargetUrl??url??"";
   if(DestinationCatalog.Find(DestinationKey) is null||TargetUrl.Length==0)validBinding=false;
  }
  else
  {
   storage=previous is null?new Dictionary<string,string>():new(previous.BackendOptions);
   Set(storage,"auth-username",AuthUsername);Set(storage,"auth-password",AuthPassword);
   Set(storage,"aws-access-key-id",AwsAccessKeyId);Set(storage,"aws-secret-access-key",AwsSecretKey);
   Set(storage,"s3-server-name",S3Server);Set(storage,"s3-region",S3Region);Set(storage,"ssh-fingerprint",SshFingerprint);
   if(Uri.TryCreate(TargetUrl,UriKind.Absolute,out var target)&&target.Scheme is "s3" or "webdav")storage["use-ssl"]="true";
  }
  SaasSource? cloud=null;
  if(Provider is not ("Files" or "NAS" or "Proxmox"))
  {
   if(!Enum.TryParse<SaasProvider>(Provider,out var kind)||!Enum.IsDefined(kind))validBinding=false;
   else
   {
    var credentials=previous?.Saas is {} old&&old.Provider==kind?new Dictionary<string,string>(old.Credentials):new();
    if(kind==SaasProvider.Microsoft365){Set(credentials,"office365-client-id",ClientId);Set(credentials,"office365-client-secret",ClientSecret);}
    else
    {
     Set(credentials,"google-admin-email",AdminEmail);
     if(!string.IsNullOrWhiteSpace(ServiceAccountJson)){credentials.Clear();credentials["google-admin-email"]=AdminEmail??"";credentials["google-service-account-json"]=ServiceAccountJson;}
     else if(!credentials.ContainsKey("google-service-account-json")){Set(credentials,"google-client-id",ClientId);Set(credentials,"google-client-secret",ClientSecret);Set(credentials,"google-refresh-token",RefreshToken);}
    }
    cloud=new(kind,DirectoryTenant??"",credentials,RootTypes,UserTypes);
   }
  }
  SourceMountRequirement[]? sourceMounts=null;ProxmoxSource? proxmox=null;
  if(Provider=="NAS")
  {
   var mounts=Lines(SourceMounts).Select(x=>x.Split('|')).ToArray();
   if(mounts.Length==0||mounts.Any(x=>x.Length!=3))validBinding=false;
   else sourceMounts=mounts.Select(x=>new SourceMountRequirement(x[0],x[1],x[2])).ToArray();
  }
  if(Provider=="Proxmox")
  {
   var ids=(ProxmoxGuestIds??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
   if(ids.Length==0||ids.Any(x=>!int.TryParse(x,out _)))validBinding=false;
   else proxmox=new(ids.Select(x=>int.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray());
  }
  var definition=new ManagedBackupDefinition(Name,cloud is null&&proxmox is null?Lines(Sources):[],TargetUrl??"",
   string.IsNullOrEmpty(Passphrase)?previous?.Passphrase??"":Passphrase,storage,KeepVersions,
   (previous?.Filters.Where(x=>x.Include)??[]).Concat(Lines(Excludes).Select(x=>new BackupFilter(false,x))).ToArray(),FirstRun is {} first?new(first.ToUniversalTime(),RepeatHours,Days):null,cloud,sourceMounts,proxmox);
  // Remove all posted secret values before redisplaying any validation error.
  Passphrase=ClientSecret=RefreshToken=ServiceAccountJson=AuthPassword=AwsSecretKey=null;ModelState.Clear();
  if(!validBinding||!ConfigurationPolicy.Valid(definition))
  {
   Error=DestinationCatalog.Find(DestinationForm.Parse(definition.TargetUrl).Key) is {} t&&!DestinationCatalog.TransportSecure(DestinationForm.Parse(definition.TargetUrl).Key,storage)
    ?"Das Ziel ist ohne gesicherte Verbindung konfiguriert. Bitte TLS bzw. den Host-Key-Fingerprint angeben."
    :"Bitte Quelle, Ziel, Zugangsdaten, Verschlüsselung und Zeitplan prüfen.";
   ShowDestination(definition with{BackendOptions=storage.Where(x=>DestinationCatalog.Find(DestinationKey)?.Option(x.Key) is {Secret:false}).ToDictionary()});
   if(previous is not null)foreach(var key in previous.BackendOptions.Keys)if(DestinationCatalog.Find(DestinationKey)?.Option(key) is {Secret:true} o)StoredSecrets.Add(o.Name);
   return Page();
  }
  var result=ManagedJobId is {} id?await ConfigurationApi.Update(id,new(Revision,definition),db,secrets,HttpContext):await ConfigurationApi.Create(DeviceId,new(0,definition),db,secrets,HttpContext);
  if(result is IStatusCodeHttpResult {StatusCode:>=400}){Error="Die Konfiguration wurde abgelehnt. Bei einer geänderten Quelle, einem neuen Ziel oder Passwort bitte einen neuen Backup-Job anlegen.";return Page();}
  return RedirectToPage("/Device",new{id=DeviceId});
 }
 private async Task<ManagedBackupDefinition?> LoadPrevious()
 {
  if(ManagedJobId is not {} id)return null;
  var job=await db.ManagedJobs.SingleOrDefaultAsync(x=>x.Id==id&&x.DeviceId==DeviceId);if(job is null)return null;
  if(HttpContext.Request.Method=="GET")Revision=job.LatestRevision;
  var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==id&&x.Revision==job.LatestRevision);
  return ConfigurationApi.Read(secrets,revision);
 }
 private void ShowDestination(ManagedBackupDefinition d)
 {
  Destination=DestinationForm.Parse(d.TargetUrl);DestinationKey=Destination.Key;
  var type=DestinationCatalog.Find(Destination.Key);
  foreach(var (key,value) in d.BackendOptions)
   if(type?.Option(key) is {} o){if(o.Secret)StoredSecrets.Add(o.Name);else OptionValues[o.Name]=value;}
 }
 private static string[] Lines(string? text)=>(text??"").Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
 private static void Set(Dictionary<string,string> destination,string key,string? value){if(!string.IsNullOrEmpty(value))destination[key]=value;}
}
