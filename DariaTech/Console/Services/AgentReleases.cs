using System.Text.Json;
using System.Text.RegularExpressions;
namespace DariaTech.Console.Services;

public sealed record AgentAsset(string Platform,string Label,string File,string Sha256,long Size,string Url);
public sealed record AgentRelease(string Version,string Tag,DateTimeOffset Created,AgentAsset[] Assets,bool Prerelease,string Page);

// Reads agent-release.json of the newest "agent-v*" GitHub release (published by the "Release agents" workflow).
// Cached; a failed refresh keeps the last good manifest. Only https://github.com/<repo>/releases/download/ URLs
// and SHA-256 values in the expected format are accepted.
public sealed partial class AgentReleases(IConfiguration config,ILogger<AgentReleases> log)
{
 public static readonly string[] Platforms=["win-x64","osx-arm64","osx-x64","linux-x64","linux-arm64"];
 static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(10),DefaultRequestHeaders={{"User-Agent","DariaTech-Backup-Console"},{"Accept","application/vnd.github+json"}}};
 readonly SemaphoreSlim gate=new(1,1);
 AgentRelease? cached;DateTimeOffset fetched;
 public string Repository=>config["Agents:Repository"] is {Length:>0} r&&Repo().IsMatch(r)?r:"DariaTech-de/DariaTech-Backup";
 public string ReleasesPage=>$"https://github.com/{Repository}/releases";
 string ApiBase=>config["Agents:ApiBase"] is {Length:>0} a?a.TrimEnd('/'):"https://api.github.com";
 bool IncludePrereleases=>!string.Equals(config["Agents:IncludePrereleases"],"false",StringComparison.OrdinalIgnoreCase);

 public async Task<AgentRelease?> Latest(CancellationToken ct=default)
 {
  if(cached is not null&&DateTimeOffset.UtcNow-fetched<TimeSpan.FromMinutes(15))return cached;
  await gate.WaitAsync(ct);
  try
  {
   if(cached is not null&&DateTimeOffset.UtcNow-fetched<TimeSpan.FromMinutes(15))return cached;
   fetched=DateTimeOffset.UtcNow; // also throttles retries after a failure
   using var list=JsonDocument.Parse(await Http.GetStringAsync($"{ApiBase}/repos/{Repository}/releases?per_page=30",ct));
   foreach(var release in list.RootElement.EnumerateArray())
   {
    var tag=release.GetProperty("tag_name").GetString()??"";var pre=release.GetProperty("prerelease").GetBoolean();
    if(!tag.StartsWith("agent-v",StringComparison.Ordinal)||release.GetProperty("draft").GetBoolean()||pre&&!IncludePrereleases)continue;
    var manifest=release.GetProperty("assets").EnumerateArray().FirstOrDefault(a=>a.GetProperty("name").GetString()=="agent-release.json");
    if(manifest.ValueKind!=JsonValueKind.Object)continue;
    var parsed=Parse(await Http.GetStringAsync(manifest.GetProperty("browser_download_url").GetString()!,ct),Repository,tag,pre,release.GetProperty("html_url").GetString()??ReleasesPage);
    if(parsed is not null){cached=parsed;break;}
   }
  }
  catch(Exception error)when(error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
  {
   log.LogWarning("Agent release manifest unavailable ({Type})",error.GetType().Name);
  }
  finally{gate.Release();}
  return cached;
 }

 internal void Use(AgentRelease release){cached=release;fetched=DateTimeOffset.UtcNow;}
 internal static AgentRelease? Parse(string json,string repository,string tag,bool prerelease,string page)
 {
  var m=JsonSerializer.Deserialize<Manifest>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
  if(m is null||m.Tag!=tag||!Version().IsMatch(m.Version??"")||m.Assets is null)return null;
  var prefix=$"https://github.com/{repository}/releases/download/{tag}/";
  var assets=m.Assets.Where(a=>a is not null&&Platforms.Contains(a.Platform)&&Sha().IsMatch(a.Sha256??"")&&a.Url is {} u&&u.StartsWith(prefix,StringComparison.Ordinal)&&!u[prefix.Length..].Contains('/')&&a.Size>0)
   .GroupBy(a=>a.Platform).Select(g=>g.First()).Select(a=>new AgentAsset(a.Platform!,a.Label??a.Platform!,a.File??"",a.Sha256!.ToLowerInvariant(),a.Size,a.Url!)).ToArray();
  return assets.Length==0?null:new(m.Version!,tag,m.Created??DateTimeOffset.UtcNow,assets,prerelease,page);
 }
 sealed record ManifestAsset(string? Platform,string? Label,string? File,string? Sha256,long Size,string? Url);
 sealed record Manifest(string? Version,string? Tag,DateTimeOffset? Created,ManifestAsset[]? Assets);
 [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]private static partial Regex Repo();
 [GeneratedRegex(@"^\d+\.\d+\.\d+$")]private static partial Regex Version();
 [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]private static partial Regex Sha();

 public static string PlatformLabel(string platform)=>platform switch
 {
  "win-x64"=>"Windows (10/11, Server 2019+)","osx-arm64"=>"macOS mit Apple-Chip (M1–M4)","osx-x64"=>"macOS mit Intel-Prozessor",
  "linux-x64"=>"Linux x64 (systemd)","linux-arm64"=>"Linux ARM64 (systemd, z. B. Raspberry Pi 4/5)",_=>platform,
 };
 // Ready-to-run install commands. The token never appears in a process argument list: it is written to a
 // root-only (Unix) or user-profile (Windows) file that the installer consumes and deletes.
 public static string Command(string platform,AgentAsset? asset,string consoleUrl,string token)
 {
  var url=asset?.Url??"<Download-URL>";var sha=asset?.Sha256??"<SHA256>";
  if(platform=="win-x64")return string.Join("\n",
   "# PowerShell als Administrator",
   "$f=\"$env:TEMP\\DariaTechBackupSetup.exe\"; $t=\"$env:TEMP\\dariatech-token.txt\"",
   $"Invoke-WebRequest -UseBasicParsing \"{url}\" -OutFile $f",
   $"if((Get-FileHash $f -Algorithm SHA256).Hash -ne \"{sha.ToUpperInvariant()}\"){{throw \"Prüfsumme stimmt nicht\"}}",
   $"Set-Content -NoNewline -Path $t -Value \"{token}\"",
   $"Start-Process $f -ArgumentList \"/console={consoleUrl}\",\"/tokenfile=$t\" -Wait",
   "Remove-Item $t -ErrorAction SilentlyContinue");
  var mac=platform.StartsWith("osx",StringComparison.Ordinal);
  var tmp=mac?"/private/tmp":"/tmp";var check=mac?"shasum -a 256 -c -":"sha256sum -c -";
  return string.Join("\n",
   $"# Terminal ({(mac?"macOS":"Linux")}), Benutzer mit sudo-Rechten",
   $"curl -fsSL -o {tmp}/dariatech-agent.tar.gz \"{url}\"",
   $"echo \"{sha}  {tmp}/dariatech-agent.tar.gz\" | {check}",
   $"d=$(sudo mktemp -d {tmp}/dariatech.XXXXXXXX) && sudo tar -xzf {tmp}/dariatech-agent.tar.gz -C \"$d\"",
   "sudo install -m 600 /dev/null \"$d/token\" && printf %s '"+token+"' | sudo tee \"$d/token\" >/dev/null",
   $"sudo \"$d/install.sh\" --console-url '{consoleUrl}' --enrollment-token-file \"$d/token\"; sudo rm -rf \"$d\" {tmp}/dariatech-agent.tar.gz");
 }
}
