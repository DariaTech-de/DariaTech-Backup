using System.Text.RegularExpressions;
namespace DariaTech.Contracts;

public sealed record BackupFilter(bool Include,string Expression);
public sealed record BackupSchedule(DateTimeOffset Start,int RepeatHours,DayOfWeek[] Days);
// Secrets enter through authenticated TLS and are encrypted at rest. Never return this DTO to management GETs.
public sealed record ManagedBackupDefinition(string Name,string[] Sources,string TargetUrl,string Passphrase,
    Dictionary<string,string> BackendOptions,int KeepVersions,BackupFilter[] Filters,BackupSchedule? Schedule,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SaasSource? Saas=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SourceMountRequirement[]? SourceMounts=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ProxmoxSource? Proxmox=null,
    // Recovery copy of another device's backup: same destination and passphrase, no schedule, never runs a backup.
    // The engine lists versions and restores directly from the destination without a local database.
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? RestoreOnly=null);
public sealed record ConfigurationInput(long ExpectedRevision,ManagedBackupDefinition Definition);
public sealed record ConfigurationAssignment(Guid JobId,long Revision,ManagedBackupDefinition Definition);
public sealed record ConfigurationReceipt(long Revision,string? LocalJobId,string Status);

public static class ConfigurationPolicy
{
 // Destinations and their options come from DestinationCatalog, generated from the engine's backends.
 // Options that read device files or weaken TLS (ssh-keyfile, accept-any-ssl-certificate, ...) are not in the catalog.
 public static bool Valid(ManagedBackupDefinition? d)
 {
  if(d is null||!SourcePolicy.Valid(d)||!Text(d.Name,200)||d.Sources is null||(d.Saas is null&&d.Proxmox is null?(d.Sources.Length is <1 or >100||d.Sources.Any(x=>!PathText(x))):(d.Sources.Length!=0||d.Saas is not null&&!SaasPolicy.Valid(d.Saas)))
    ||!Text(d.Passphrase,200)||d.Passphrase.Length<14||d.KeepVersions is <1 or >10000
    ||!Uri.TryCreate(d.TargetUrl,UriKind.Absolute,out var uri)||DestinationCatalog.Find(uri.Scheme) is not {} destination
    ||!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment)
    ||d.BackendOptions is null||d.BackendOptions.Count>100||!DestinationCatalog.ValidOptions(destination,d.BackendOptions)||!DestinationCatalog.TransportSecure(uri.Scheme,d.BackendOptions)
    ||d.Filters is null||d.Filters.Length>100||d.Filters.Any(x=>x is null||!Text(x.Expression,1000)))return false;
  if(uri.Scheme=="file"&&!string.IsNullOrEmpty(uri.Host)&&uri.Host!="localhost")return false;
  if(d.RestoreOnly is false||d.RestoreOnly is true&&(d.Schedule is not null||d.Saas is not null||d.Proxmox is not null||d.SourceMounts is not null))return false;
  if(d.Schedule is {} s&&(s.RepeatHours is <1 or >8760||s.Days is null||s.Days.Length is <1 or >7||s.Days.Any(x=>!Enum.IsDefined(x))||s.Days.Distinct().Count()!=s.Days.Length||s.Start.Year is <2020 or >2100))return false;
  return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(d).Length<=80000;
 }
 // A destination on its own, validated with the same rules as inside a job definition.
 public static bool ValidDestination(string? url,Dictionary<string,string>? options)=>url is not null&&options is not null&&
  Valid(new ManagedBackupDefinition("destination",["/destination"],url,"destination-validation-only",options,1,[],null));
 private static bool PathText(string? s)=>Text(s,1000)&& (s!.StartsWith('/')||Regex.IsMatch(s,@"^[A-Za-z]:[\\/]")||Regex.IsMatch(s,@"^\\\\[^\\/]+\\[^\\/]+(?:\\|$)"));
 private static bool Text(string? s,int max)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=max&&!s.Any(char.IsControl);
}
