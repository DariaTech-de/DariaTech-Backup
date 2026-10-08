namespace DariaTech.Contracts;

// Mount identities contain no credentials; the OS mounts the share under local administrator control.
public sealed record SourceMountRequirement(string MountPoint,string FileSystemType,string RemoteSource);
public sealed record ProxmoxSource(int[] GuestIds);

public static class SourcePolicy
{
 public static bool Valid(ManagedBackupDefinition d)
 {
  if(d.SourceMounts is {} mounts&&(d.Saas is not null||d.Proxmox is not null||mounts.Length is <1 or >100||mounts.Any(x=>!ValidMount(x))||mounts.Select(x=>x.MountPoint).Distinct(StringComparer.Ordinal).Count()!=mounts.Length))return false;
  if(d.Proxmox is {} p&&(d.Saas is not null||d.SourceMounts is not null||d.Sources is not {Length:0}||d.Filters is not {Length:0}||p.GuestIds is not {Length:>0 and <=100}||p.GuestIds.Any(x=>x is <100 or >999999999)||p.GuestIds.Distinct().Count()!=p.GuestIds.Length))return false;
  return true;
 }
 public static bool ValidMount(SourceMountRequirement? mount)=>mount is not null&&
  mount.MountPoint is not null&&mount.MountPoint.StartsWith('/')&&mount.MountPoint.Length is >1 and <=1000&&!mount.MountPoint.Any(char.IsControl)&&
  mount.FileSystemType is "cifs" or "smbfs" or "nfs" or "nfs4"&&
  !string.IsNullOrWhiteSpace(mount.RemoteSource)&&mount.RemoteSource.Length<=1000&&!mount.RemoteSource.Any(char.IsControl)&&
  (!mount.RemoteSource.Contains('@')||mount.RemoteSource.StartsWith("//",StringComparison.Ordinal)&&!mount.RemoteSource[2..mount.RemoteSource.IndexOf('@')].Contains(':')); // A macOS SMB username is allowed; passwords in user-info are rejected.
}

public sealed record ProxmoxRestoreSelection(long ConfigurationRevision,DateTimeOffset Snapshot,string ArchivePath,int TargetGuestId,string Storage,bool ConfirmImport);
public static class ProxmoxPolicy
{
 public static bool ValidRestore(ProxmoxRestoreSelection? s,DateTimeOffset now)=>s is not null&&s.ConfigurationRevision>0&&s.ConfirmImport&&
  s.Snapshot.Year>=2000&&s.Snapshot<=now.AddMinutes(5)&&s.TargetGuestId is >=100 and <=999999999&&
  !string.IsNullOrWhiteSpace(s.ArchivePath)&&s.ArchivePath.Length<=1000&&!s.ArchivePath.Any(char.IsControl)&&
  !string.IsNullOrWhiteSpace(s.Storage)&&s.Storage.Length<=100&&s.Storage.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
}
