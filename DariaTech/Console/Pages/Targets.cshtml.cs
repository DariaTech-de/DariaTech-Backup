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
public sealed class TargetsModel(ManagementDb db,ISecretStore secrets):PageModel
{
 public List<DestinationTemplate> Items {get;private set;}=[];
 public DestinationTemplate? Editing {get;private set;}
 [BindProperty]public Guid? Id {get;set;}
 [BindProperty]public string? Name {get;set;}
 [BindProperty]public bool IsDefault {get;set;}
 [BindProperty]public string? DestinationKey {get;set;}
 public DestinationForm.Picker Picker {get;private set;}=new("file",new("file","","",""),new Dictionary<string,string>(),new HashSet<string>(),false);
 public string? Error {get;private set;}
 public async Task<IActionResult> OnGetAsync(Guid? edit)
 {
  await Load();
  if(edit is not null){Editing=Items.SingleOrDefault(x=>x.Id==edit);if(Editing is null)return NotFound();Id=Editing.Id;Name=Editing.Name;IsDefault=Editing.IsDefault;Show(Editing.TargetUrl,DestinationTemplates.Options(secrets,Editing));}
  else IsDefault=Items.Count==0;
  return Page();
 }
 public async Task<IActionResult> OnPostSaveAsync()
 {
  await Load();
  var existing=Id is {} id?Items.SingleOrDefault(x=>x.Id==id):null;
  if(Id is not null&&existing is null)return NotFound();
  var previous=existing is null?null:DestinationTemplates.Options(secrets,existing);
  var (url,options)=DestinationForm.Read(Request.Form,DestinationKey??"",previous);
  // Validate exactly like a job definition would be validated, so a saved destination is always usable.
  var probe=new ManagedBackupDefinition("probe",["/probe"],url??"","validation-only-passphrase",options,1,[],null);
  if(!ManagementApi.Text(Name,200)||url is null||!ConfigurationPolicy.Valid(probe))
  {
   Error=url is not null&&!DestinationCatalog.TransportSecure(DestinationForm.Parse(url).Key,options)?"Das Ziel ist ohne gesicherte Verbindung konfiguriert. Bitte TLS bzw. den Host-Key-Fingerprint angeben.":"Bitte Name, Ziel und Zugangsdaten prüfen.";
   Editing=existing;Show(url??"",options.Where(x=>DestinationCatalog.Find(DestinationKey)?.Option(x.Key) is {Secret:false}).ToDictionary(),previous);ModelState.Clear();return Page();
  }
  if(Items.Any(x=>x.Name==Name&&x.Id!=existing?.Id)){Error="Ein Speicherziel mit diesem Namen existiert bereits.";Editing=existing;Show(url,options);ModelState.Clear();return Page();}
  var target=existing??new DestinationTemplate();
  target.Name=Name!;target.TargetUrl=url;DestinationTemplates.SetOptions(secrets,target,options);
  if(existing is null)db.DestinationTemplates.Add(target);
  if(IsDefault){foreach(var other in Items.Where(x=>x.IsDefault&&x.Id!=target.Id))other.IsDefault=false;await db.SaveChangesAsync();}
  target.IsDefault=IsDefault;
  ManagementApi.Audit(db,HttpContext,null,existing is null?"destination-template.created":"destination-template.changed",target.Id);
  await db.SaveChangesAsync();return RedirectToPage();
 }
 public async Task<IActionResult> OnPostDeleteAsync(Guid id)
 {
  var target=await db.DestinationTemplates.SingleOrDefaultAsync(x=>x.Id==id);if(target is null)return NotFound();
  db.DestinationTemplates.Remove(target);ManagementApi.Audit(db,HttpContext,null,"destination-template.deleted",id);await db.SaveChangesAsync();return RedirectToPage();
 }
 private async Task Load()=>Items=await db.DestinationTemplates.OrderByDescending(x=>x.IsDefault).ThenBy(x=>x.Name).ToListAsync();
 private void Show(string url,IReadOnlyDictionary<string,string> options,IReadOnlyDictionary<string,string>? stored=null)
 {
  var parts=DestinationForm.Parse(url);if(parts.Key.Length==0)parts=new(DestinationKey??"file","","","");
  var type=DestinationCatalog.Find(parts.Key);
  var values=options.Where(x=>type?.Option(x.Key) is {Secret:false}).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.OrdinalIgnoreCase);
  var secretsStored=(stored??options).Keys.Where(k=>type?.Option(k) is {Secret:true}).ToHashSet(StringComparer.OrdinalIgnoreCase);
  Picker=new(parts.Key,parts,values,secretsStored,false);
 }
}
