using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DariaTech.Contracts;
namespace DariaTech.Agent;

// Device-level signed commands that help configure a job from the Console. Both run only when remote
// commands are enabled locally, return folder names or a test outcome, and never return file contents,
// file names or raw engine error text.
public sealed partial class DuplicatiAdapter
{
 public async Task<RestoreCatalog> BrowseFolders(DeviceCommand command,CancellationToken ct)
 {
  if(command.Action!=RemoteAction.BrowseFolders||command.Catalog is null)throw new InvalidOperationException("Invalid browse command");
  var path=command.Catalog.Prefix??"/";
  using var response=await client.PostAsJsonAsync("/api/v1/filesystem?onlyFolders=true&showHidden=false",new{path},ct);
  if(response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)throw new InvalidOperationException("Folder not available");
  response.EnsureSuccessStatusCode();
  using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  var folders=new List<RestoreFile>();var truncated=false;
  foreach(var node in result.RootElement.EnumerateArray())
  {
   var id=Get(node,"id").ToString();
   // Engine pseudo-entries such as %MY_DOCUMENTS% are expanded locally and would not be portable as job sources.
   if(string.IsNullOrWhiteSpace(id)||id.Length>1000||id.Any(char.IsControl)||id.Contains('%'))continue;
   if(folders.Count>=500){truncated=true;break;}
   folders.Add(new(id,null,true));
  }
  var catalog=new RestoreCatalog([],folders.ToArray(),truncated);
  while(JsonSerializer.SerializeToUtf8Bytes(catalog).Length>50000&&catalog.Files.Length>0)catalog=catalog with{Files=catalog.Files[..^1],Truncated=true};
  return catalog;
 }

 public async Task<CommandReceipt> TestDestination(DeviceCommand command,CancellationToken ct)
 {
  if(command.Action!=RemoteAction.TestDestination||command.Test is not {} test||!ConfigurationPolicy.ValidDestination(test.TargetUrl,test.Options))throw new InvalidOperationException("Invalid destination test");
  // The engine's test endpoint takes backend options in the URL query, exactly like its own UI.
  var url=test.TargetUrl+(test.Options.Count==0?"":"?"+string.Join('&',test.Options.Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value))));
  using var response=await client.PostAsJsonAsync($"/api/v1/remoteoperation/test?autocreate={(test.CreateFolder?"true":"false")}&readOnlyTest={(test.CreateFolder?"false":"true")}",new{path=url},ct);
  if(response.IsSuccessStatusCode)return new("Completed",null,null);
  var body=await response.Content.ReadAsStringAsync(ct);
  if(body.Contains("missing-folder",StringComparison.Ordinal)||body.Contains("error-creating-folder",StringComparison.Ordinal))return new("Failed",null,"DestinationFolderMissing");
  // A host key or certificate fingerprint is public information and lets the operator pin the real server.
  if(HostKey().Match(body) is {Success:true} key)return new("Failed",null,"DestinationHostKeyMismatch",null,key.Groups[1].Value);
  if(Certificate().Match(body) is {Success:true} cert)return new("Failed",null,"DestinationCertificateInvalid",null,cert.Groups[1].Value);
  return new("Failed",null,"DestinationTestFailed");
 }
 [GeneratedRegex(@"incorrect-host-key:\\?""([A-Za-z0-9+/=:\- ]{8,300})\\?""")]private static partial Regex HostKey();
 [GeneratedRegex(@"incorrect-cert:([A-Fa-f0-9:]{16,200})")]private static partial Regex Certificate();
}
