using DariaTech.Console.Api;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using DariaTech.Console.Services;
using DariaTech.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Pages;

// Apply the same schedule, retention or allowed backup age to several backups of one customer. Only the ticked
// settings change; sources, exclusions, destination and passphrase of every job stay as they are, so individual
// deviations are kept. The change is previewed per job before it is saved, and saving is not applying: each
// device takes the new revision on its next contact.
[Authorize(Policy="Admin")]
public sealed class SharedSettingsModel(ManagementDb db,ISecretStore secrets):PageModel
{
 public sealed record Row(ManagedJob Job,string Device,ManagedBackupDefinition Definition);
 public sealed record Change(Row Row,string Before,string After,DateTimeOffset? Start);
 public Customer Customer {get;private set;}=null!;public List<Row> Rows {get;private set;}=[];public List<Change> Preview {get;private set;}=[];
 public List<string> Results {get;private set;}=[];public string? Error {get;private set;}
 [BindProperty]public Guid[] JobIds {get;set;}=[];
 [BindProperty]public bool ChangeSchedule {get;set;}[BindProperty]public DateTime? FirstRun {get;set;}[BindProperty]public int RepeatHours {get;set;}=24;
 [BindProperty]public DayOfWeek[] Days {get;set;}=Enum.GetValues<DayOfWeek>();[BindProperty]public int StaggerMinutes {get;set;}
 [BindProperty]public bool ChangeRetention {get;set;}[BindProperty]public int KeepVersions {get;set;}=30;
 [BindProperty]public bool ChangeMaxAge {get;set;}[BindProperty]public int? MaxBackupAgeHours {get;set;}
 [BindProperty]public bool Confirm {get;set;}
 // Storage credentials: copy a template's current options (e.g. rotated password) into existing jobs that write
 // below that template. Only jobs of this customer, only the selected ones, destination folder unchanged.
 [BindProperty]public bool ChangeCredentials {get;set;}[BindProperty]public Guid? TemplateId {get;set;}
 public List<DestinationTemplate> Templates {get;private set;}=[];
 public static bool Below(string jobUrl,string templateUrl)
 {
  var j=DestinationForm.Parse(jobUrl);var t=DestinationForm.Parse(templateUrl);
  return j.Key==t.Key&&string.Equals(j.Host,t.Host,StringComparison.OrdinalIgnoreCase)&&j.Port==t.Port&&(j.Path.Trim('/')+"/").StartsWith(t.Path.Trim('/')+"/",StringComparison.Ordinal);
 }
 async Task<bool> Load(Guid id)
 {
  var c=await db.Customers.SingleOrDefaultAsync(x=>x.Id==id);if(c is null)return false;Customer=c;
  var devices=await db.Devices.Where(x=>x.TenantId==c.TenantId&&x.Active).ToDictionaryAsync(x=>x.Id,x=>x.Name);
  var jobs=await db.ManagedJobs.Where(x=>x.TenantId==c.TenantId&&!x.RestoreOnly&&devices.Keys.Contains(x.DeviceId)).ToListAsync();
  var ids=jobs.Select(x=>x.Id).ToList();var revisions=await db.ConfigurationRevisions.Where(x=>ids.Contains(x.ManagedJobId)).ToListAsync();
  foreach(var j in jobs)if(revisions.SingleOrDefault(x=>x.ManagedJobId==j.Id&&x.Revision==j.LatestRevision) is {} r){var d=ConfigurationApi.Read(secrets,r);if(d.Saas is null&&d.Proxmox is null)Rows.Add(new(j,devices[j.DeviceId],d));}
  Rows=Rows.OrderBy(x=>x.Device).ThenBy(x=>x.Job.Name).ToList();Templates=await DestinationTemplates.For(db.DestinationTemplates,c.TenantId).ToListAsync();return true;
 }
 public async Task<IActionResult> OnGetAsync(Guid id)=>await Load(id)?Page():NotFound();
 static string Describe(ManagedBackupDefinition d,int? maxAge)=>
  $"{(d.Schedule is {} s?$"ab {Ui.LocalDate(s.Start)}, alle {s.RepeatHours} Std.{(s.Days.Length==7?"":" ("+string.Join(", ",s.Days.Select(x=>System.Globalization.CultureInfo.GetCultureInfo("de-DE").DateTimeFormat.GetAbbreviatedDayName(x)))+")")}":"Manuell")} · {d.KeepVersions} Versionen · Alter {(maxAge is int h?$"{h} Std.":"aus Zeitplan")}";
 DestinationTemplate? Template=>Templates.SingleOrDefault(x=>x.Id==TemplateId);
 List<Change> Plan()
 {
  var selected=Rows.Where(x=>JobIds.Contains(x.Job.Id)&&(!ChangeCredentials||Template is {} t&&Below(x.Definition.TargetUrl,t.TargetUrl))).ToList();var changes=new List<Change>();
  for(var i=0;i<selected.Count;i++)
  {
   var row=selected[i];var next=Apply(row.Definition,i);
   var credentials=ChangeCredentials?" · Zugangsdaten aus „"+Template!.Name+"“":"";
   changes.Add(new(row,Describe(row.Definition,row.Job.MaxBackupAgeHours),Describe(next,ChangeMaxAge?MaxBackupAgeHours:row.Job.MaxBackupAgeHours)+credentials,next.Schedule?.Start));
  }
  return changes;
 }
 ManagedBackupDefinition Apply(ManagedBackupDefinition d,int index)
 {
  var next=d;
  if(ChangeSchedule)next=next with{Schedule=FirstRun is {} first?new(Ui.FromLocal(first).AddMinutes(index*Math.Clamp(StaggerMinutes,0,720)),RepeatHours,Days):null};
  if(ChangeRetention)next=next with{KeepVersions=KeepVersions};
  if(ChangeCredentials&&Template is {} t)next=next with{BackendOptions=DestinationTemplates.Options(secrets,t)};
  return next;
 }
 string? Validate()
 {
  if(JobIds.Length==0)return "Bitte mindestens ein Backup auswählen.";
  if(!ChangeSchedule&&!ChangeRetention&&!ChangeMaxAge&&!ChangeCredentials)return "Bitte angeben, welche Einstellung geändert werden soll.";
  if(ChangeCredentials&&Template is null)return "Bitte die Speichervorlage wählen, deren Zugangsdaten übernommen werden sollen.";
  if(ChangeCredentials&&Plan().Count==0)return "Keines der ausgewählten Backups speichert unterhalb dieser Vorlage.";
  if(ChangeSchedule&&FirstRun is not null&&(RepeatHours is <1 or >8760||Days.Length==0))return "Bitte Wiederholung und Wochentage angeben.";
  if(ChangeRetention&&KeepVersions is <1 or >10000)return "Die Aufbewahrung muss zwischen 1 und 10000 Versionen liegen.";
  if(ChangeMaxAge&&MaxBackupAgeHours is <1 or >8760)return "Das zulässige Backup-Alter muss zwischen 1 und 8760 Stunden liegen (leer = aus dem Zeitplan).";
  if(StaggerMinutes is <0 or >720)return "Der Versatz muss zwischen 0 und 720 Minuten liegen.";
  return null;
 }
 public async Task<IActionResult> OnPostPreviewAsync(Guid id)
 {
  if(!await Load(id))return NotFound();Error=Validate();if(Error is null)Preview=Plan();return Page();
 }
 public async Task<IActionResult> OnPostApplyAsync(Guid id)
 {
  if(!await Load(id))return NotFound();Error=Validate();if(Error is not null)return Page();
  Preview=Plan();if(!Confirm){Error="Bitte die Änderung für die aufgeführten Backups bestätigen.";return Page();}
  for(var i=0;i<Preview.Count;i++)
  {
   var row=Preview[i].Row;var next=Apply(row.Definition,i);
   var result=await ConfigurationApi.Update(row.Job.Id,new(row.Job.LatestRevision,next),db,secrets,HttpContext);
   if(result is IStatusCodeHttpResult {StatusCode:>=400} failed){Results.Add($"{row.Device} · {row.Job.Name}: nicht gespeichert (HTTP {failed.StatusCode}) – inzwischen geändert? Seite neu laden.");continue;}
   if(ChangeMaxAge){var job=await db.ManagedJobs.SingleAsync(x=>x.Id==row.Job.Id);job.MaxBackupAgeHours=MaxBackupAgeHours;await db.SaveChangesAsync();}
   Results.Add($"{row.Device} · {row.Job.Name}: gespeichert – wird beim nächsten Kontakt des Geräts angewendet.");
  }
  ManagementApi.Audit(db,HttpContext,Customer.TenantId,"backup.shared-settings-applied",Customer.Id);await db.SaveChangesAsync();
  Preview=[];return Page();
 }
}
