using System.Text.RegularExpressions;
namespace DariaTech.Contracts;

public sealed record BackupFilter(bool Include,string Expression);
public sealed record BackupSchedule(DateTimeOffset Start,int RepeatHours,DayOfWeek[] Days);
// Secrets enter through authenticated TLS and are encrypted at rest. Never return this DTO to management GETs.
public sealed record ManagedBackupDefinition(string Name,string[] Sources,string TargetUrl,string Passphrase,
    Dictionary<string,string> BackendOptions,int KeepVersions,BackupFilter[] Filters,BackupSchedule? Schedule,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SaasSource? Saas=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SourceMountRequirement[]? SourceMounts=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ProxmoxSource? Proxmox=null);
public sealed record ConfigurationInput(long ExpectedRevision,ManagedBackupDefinition Definition);
public sealed record ConfigurationAssignment(Guid JobId,long Revision,ManagedBackupDefinition Definition);
public sealed record ConfigurationReceipt(long Revision,string? LocalJobId,string Status);

public static class ConfigurationPolicy
{
 private static readonly HashSet<string> Options = new(StringComparer.Ordinal) {
  "auth-username","auth-password","aws-access-key-id","aws-secret-access-key","s3-server-name",
  "s3-region","s3-location-constraint","s3-client","use-ssl","ssh-fingerprint","ssh-keyfile","ssh-keyfile-password"
 };
 public static bool Valid(ManagedBackupDefinition? d)
 {
  if(d is null||!SourcePolicy.Valid(d)||!Text(d.Name,200)||d.Sources is null||(d.Saas is null&&d.Proxmox is null?(d.Sources.Length is <1 or >100||d.Sources.Any(x=>!PathText(x))):(d.Sources.Length!=0||d.Saas is not null&&!SaasPolicy.Valid(d.Saas)))
    ||!Text(d.Passphrase,200)||d.Passphrase.Length<14||d.KeepVersions is <1 or >10000
    ||!Uri.TryCreate(d.TargetUrl,UriKind.Absolute,out var uri)||uri.Scheme is not ("file" or "s3" or "ssh" or "webdav" or "webdavs")
    ||!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment)
    ||d.BackendOptions is null||d.BackendOptions.Count>Options.Count||d.BackendOptions.Any(x=>!Options.Contains(x.Key)||!Text(x.Value,2000))
    ||d.Filters is null||d.Filters.Length>100||d.Filters.Any(x=>x is null||!Text(x.Expression,1000)))return false;
  if(uri.Scheme=="file"&&!string.IsNullOrEmpty(uri.Host)&&uri.Host!="localhost")return false;
  if(uri.Scheme is "s3" or "webdav" && (!d.BackendOptions.TryGetValue("use-ssl",out var ssl)||ssl!="true"))return false;
  if(uri.Scheme=="ssh"&&(!d.BackendOptions.TryGetValue("ssh-fingerprint",out var fp)||!Text(fp,200)))return false;
  if(d.BackendOptions.ContainsKey("ssh-keyfile"))return false; // Central configuration cannot read arbitrary private keys from the device.
  if(d.Schedule is {} s&&(s.RepeatHours is <1 or >8760||s.Days is null||s.Days.Length is <1 or >7||s.Days.Any(x=>!Enum.IsDefined(x))||s.Days.Distinct().Count()!=s.Days.Length||s.Start.Year is <2020 or >2100))return false;
  return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(d).Length<=80000;
 }
 private static bool PathText(string? s)=>Text(s,1000)&& (s!.StartsWith('/')||Regex.IsMatch(s,@"^[A-Za-z]:[\\/]")||Regex.IsMatch(s,@"^\\\\[^\\/]+\\[^\\/]+(?:\\|$)"));
 private static bool Text(string? s,int max)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=max&&!s.Any(char.IsControl);
}
