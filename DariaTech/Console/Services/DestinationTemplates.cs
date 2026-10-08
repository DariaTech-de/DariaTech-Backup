using System.Text.Json;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
namespace DariaTech.Console.Services;

// Options of saved destinations are encrypted like job configurations; they never leave the Console except
// copied into a job configuration for its assigned agent.
public static class DestinationTemplates
{
 // Locations a job of this customer may use: the customer's own first, then those for all customers.
 public static IQueryable<DestinationTemplate> For(IQueryable<DestinationTemplate> all,Guid tenantId)=>
  all.Where(x=>x.TenantId==tenantId||x.TenantId==null).OrderBy(x=>x.TenantId==null).ThenByDescending(x=>x.IsDefault).ThenBy(x=>x.Name);
 // The customer's default wins over the default for all customers.
 public static DestinationTemplate? Preferred(IReadOnlyList<DestinationTemplate> available,Guid tenantId)=>
  available.FirstOrDefault(x=>x.TenantId==tenantId&&x.IsDefault)??available.FirstOrDefault(x=>x.TenantId==null&&x.IsDefault)??available.FirstOrDefault();
 static string Purpose(DestinationTemplate t)=>$"destination-template:{t.Id}";
 public static Dictionary<string,string> Options(ISecretStore secrets,DestinationTemplate t)=>
  t.EncryptedOptions.Length==0?new(StringComparer.OrdinalIgnoreCase):new(JsonSerializer.Deserialize<Dictionary<string,string>>(secrets.Unprotect(t.TenantId??Guid.Empty,Purpose(t),t.EncryptedOptions))!,StringComparer.OrdinalIgnoreCase);
 public static void SetOptions(ISecretStore secrets,DestinationTemplate t,Dictionary<string,string> options)=>
  t.EncryptedOptions=secrets.Protect(t.TenantId??Guid.Empty,Purpose(t),JsonSerializer.Serialize(options));
}
