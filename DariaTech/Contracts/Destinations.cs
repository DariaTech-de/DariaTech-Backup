using System.Reflection;
using System.Text.Json;
namespace DariaTech.Contracts;

// Generated from the OSS engine by scripts/generate-destinations.sh; do not edit Destinations.json by hand.
public sealed record DestinationOption(string Name,string Type,bool Secret,string? Summary,string[]? Values,string? DefaultValue);
public sealed record DestinationType(string Key,string Name,string? Description,bool Deprecated,bool Untested,DestinationOption[] Options)
{
 public DestinationOption? Option(string name)=>Options.FirstOrDefault(x=>string.Equals(x.Name,name,StringComparison.OrdinalIgnoreCase));
}

public static class DestinationCatalog
{
 private static readonly Lazy<IReadOnlyList<DestinationType>> Loaded=new(()=>
 {
  using var stream=typeof(DestinationCatalog).Assembly.GetManifestResourceStream("DariaTech.Contracts.Destinations.json")??throw new InvalidOperationException("Destination catalog missing");
  return JsonSerializer.Deserialize<DestinationType[]>(stream,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidOperationException("Destination catalog invalid");
 });
 public static IReadOnlyList<DestinationType> All=>Loaded.Value;
 public static DestinationType? Find(string? scheme)=>Resolve(scheme).Type;
 // Like the engine's BackendLoader, "<scheme>s" (webdavs, ftps, s3s) selects <scheme> with TLS forced on.
 public static (DestinationType? Type,bool ForcedTls) Resolve(string? scheme)
 {
  if(string.IsNullOrEmpty(scheme))return (null,false);
  var exact=All.FirstOrDefault(x=>string.Equals(x.Key,scheme,StringComparison.OrdinalIgnoreCase));
  if(exact is not null)return (exact,false);
  if(scheme.Length>1&&scheme.EndsWith('s')&&All.FirstOrDefault(x=>string.Equals(x.Key,scheme[..^1],StringComparison.OrdinalIgnoreCase)) is {} secure)return (secure,true);
  return (null,false);
 }

 // Transport rules on top of the engine's own option list: never push a configuration that sends
 // credentials or backup data without TLS, or that trusts an unverified SSH host.
 public static bool TransportSecure(string scheme,IReadOnlyDictionary<string,string> options)
 {
  var (type,forcedTls)=Resolve(scheme);
  if(type is null)return false;
  if(forcedTls&&type.Key is not "ssh")return true;
  scheme=type.Key;
  string? Get(string key)=>options.FirstOrDefault(x=>string.Equals(x.Key,key,StringComparison.OrdinalIgnoreCase)).Value;
  return scheme.ToLowerInvariant() switch
  {
   "s3" or "webdav" or "tahoe"=>Get("use-ssl")=="true",
   "ftp"=>Get("ftp-encryption-mode") is "Explicit" or "Implicit",
   "aftp"=>Get("aftp-encryption-mode") is "Explicit" or "Implicit",
   "ssh"=>!string.IsNullOrWhiteSpace(Get("ssh-fingerprint")),
   _=>true,
  };
 }

 public static bool ValidOptions(DestinationType type,IReadOnlyDictionary<string,string> options)
 {
  foreach(var (key,value) in options)
  {
   var option=type.Option(key);
   if(option is null||value is null||value.Length>8000||value.Any(c=>char.IsControl(c)&&c is not ('\n' or '\r')))return false;
   if(option.Type=="Boolean"&&value is not ("true" or "false"))return false;
   if(option.Type=="Integer"&&!long.TryParse(value,out _))return false;
   if(option.Type is "Enumeration"&&option.Values is {Length:>0}&&!option.Values.Contains(value,StringComparer.OrdinalIgnoreCase))return false;
   // Only secrets (keys, service-account JSON) may span lines.
   if(!option.Secret&&value.Any(c=>c is '\n' or '\r'))return false;
  }
  return options.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()==options.Count;
 }
}
