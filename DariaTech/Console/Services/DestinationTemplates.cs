using System.Text.Json;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
namespace DariaTech.Console.Services;

// Options of saved destinations are encrypted like job configurations; they never leave the Console except
// copied into a job configuration for its assigned agent.
public static class DestinationTemplates
{
 static string Purpose(DestinationTemplate t)=>$"destination-template:{t.Id}";
 public static Dictionary<string,string> Options(ISecretStore secrets,DestinationTemplate t)=>
  t.EncryptedOptions.Length==0?new(StringComparer.OrdinalIgnoreCase):new(JsonSerializer.Deserialize<Dictionary<string,string>>(secrets.Unprotect(Guid.Empty,Purpose(t),t.EncryptedOptions))!,StringComparer.OrdinalIgnoreCase);
 public static void SetOptions(ISecretStore secrets,DestinationTemplate t,Dictionary<string,string> options)=>
  t.EncryptedOptions=secrets.Protect(Guid.Empty,Purpose(t),JsonSerializer.Serialize(options));
}
