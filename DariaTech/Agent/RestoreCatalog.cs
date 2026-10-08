using System.Globalization;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;
public sealed partial class DuplicatiAdapter
{
 public async Task<RestoreCatalog> ReadCatalog(DeviceCommand command,CancellationToken ct)
 {
  if(command.Action is not (RemoteAction.ListRestorePoints or RemoteAction.ListRestoreFiles))throw new InvalidOperationException("Invalid catalog action");
  var jobs=await ReadJobs(ct);if(jobs.All(x=>x.LocalId!=command.LocalJobId))throw new InvalidOperationException("Unknown local backup");
  var url=$"/api/v1/backup/{command.LocalJobId}/"+(command.Action==RemoteAction.ListRestorePoints?"filesets":$"files?folder-contents=true&time={Uri.EscapeDataString(command.Catalog!.Snapshot!.Value.ToUniversalTime().ToString("O"))}"+(command.Catalog.Prefix is {} prefix?$"&filter={Uri.EscapeDataString(prefix)}":""));
  using var response=await client.GetAsync(url,ct);response.EnsureSuccessStatusCode();using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  var points=new List<RestorePoint>();var files=new List<RestoreFile>();var truncated=false;
  if(command.Action==RemoteAction.ListRestorePoints)
  {
   foreach(var p in result.RootElement.EnumerateArray())
   {if(points.Count>=500){truncated=true;break;}if(DateTimeOffset.TryParse(Get(p,"Time").ToString(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var time))points.Add(new(time.ToUniversalTime(),NullableNumber(Get(p,"FileCount")),NullableNumber(Get(p,"FileSizes"))));}
  }
  else
  {
   foreach(var p in Get(result.RootElement,"Files").EnumerateArray())
   {
    if(files.Count>=500){truncated=true;break;}var path=Get(p,"Path").GetString();if(string.IsNullOrWhiteSpace(path)||path.Length>1000||path.Any(char.IsControl))continue;
    var sizes=Get(p,"Sizes");files.Add(new(path,sizes.ValueKind==JsonValueKind.Array&&sizes.GetArrayLength()>0?NullableNumber(sizes[0]):null,path.EndsWith('/')||path.EndsWith('\\')));
   }
  }
  var catalog=new RestoreCatalog(points.ToArray(),files.ToArray(),truncated);
  while(JsonSerializer.SerializeToUtf8Bytes(catalog).Length>50000&&catalog.Files.Length>0)catalog=catalog with{Files=catalog.Files[..^1],Truncated=true};
  return catalog;
 }
}
