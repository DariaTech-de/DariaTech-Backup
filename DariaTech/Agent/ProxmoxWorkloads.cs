using System.Diagnostics;
using System.Text.RegularExpressions;
using DariaTech.Contracts;
namespace DariaTech.Agent;

// Uses Proxmox's own snapshot/export pipeline; never copies running guest disks.
public static class ProxmoxWorkloads
{
 public static string Authorize(AgentOptions options,Guid job,ProxmoxSource source)
 {
  if(!OperatingSystem.IsLinux()||!options.AllowProxmoxSnapshots||!options.ManageEngine||source.GuestIds.Any(x=>!options.AllowedProxmoxGuestIds.Contains(x))||options.ProxmoxDumpRoot is null)
   throw new InvalidOperationException("Guest-image backup is not locally authorized");
  if(!Directory.Exists("/etc/pve")||!File.Exists("/usr/bin/pveversion"))throw new InvalidOperationException("Install the managed Linux agent on the actual Proxmox host");
  UnixPrivatePaths.Directory(options.ProxmoxDumpRoot,false);
  foreach(var id in source.GuestIds)
  {
   var vm=File.Exists("/etc/pve/qemu-server/"+id+".conf");var container=File.Exists("/etc/pve/lxc/"+id+".conf");
   if(vm==container)throw new InvalidOperationException("Guest is missing or its type is ambiguous");
  }
  UnixPrivatePaths.TrustedFile(ExportExecutable());
  var directory=Path.Combine(options.ProxmoxDumpRoot,job.ToString("D"));UnixPrivatePaths.Directory(directory,true);
  var latest=Path.Combine(directory,"latest");UnixPrivatePaths.Directory(latest,true);return latest;
 }
 private static string ExportExecutable()
 {
  foreach(var path in new[]{"/usr/bin/vzdump","/usr/sbin/vzdump"})if(File.Exists(path))return path;
  throw new InvalidOperationException("Native Proxmox export executable is unavailable");
 }
 public static async Task Prepare(AgentOptions options,ManagedSourceBinding binding,CancellationToken ct)
 {
  if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("Proxmox guest exports require Linux");
  var source=binding.Proxmox??throw new InvalidOperationException("Proxmox selection missing");
  var latest=Authorize(options,binding.JobId,source);var root=options.ProxmoxDumpRoot!;
  var mountRequirements=options.RequiredSourceMounts.Where(m=>SourceChecks.Contains(m.MountPoint,root)||SourceChecks.Contains(root,m.MountPoint)).ToArray();
  if(mountRequirements.Length>0)SourceChecks.ValidateMounts([root],mountRequirements,await SourceChecks.ReadMounts(ct));
  // This reservation is local policy, not an arbitrary value sent by the Console.
  var drive=DriveInfo.GetDrives().Where(d=>SourceChecks.Contains(d.Name,root)).OrderByDescending(d=>d.Name.Length).FirstOrDefault()??throw new InvalidOperationException("Staging filesystem unavailable");
  if(drive.AvailableFreeSpace<options.ProxmoxMinimumFreeBytes)throw new InvalidOperationException("Insufficient reserved staging capacity");
  var lockPath=Path.Combine(root,"export.lock");if(File.Exists(lockPath))UnixPrivatePaths.File(lockPath);
  using var exportLock=new FileStream(lockPath,new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});
  var parent=Path.GetDirectoryName(latest)!;var staging=Path.Combine(parent,"export-"+Guid.NewGuid().ToString("N"));UnixPrivatePaths.Directory(staging,true);
  try
  {
   var info=new ProcessStartInfo(ExportExecutable()){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
   foreach(var id in source.GuestIds.Order())info.ArgumentList.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
   foreach(var item in new[]{"--dumpdir",staging,"--mode","snapshot","--compress","zstd","--remove","0"})info.ArgumentList.Add(item);
   using var process=Process.Start(info)??throw new InvalidOperationException("Native guest export could not start");
   var stdout=Discard(process.StandardOutput,ct);var stderr=Discard(process.StandardError,ct);
   try{await process.WaitForExitAsync(ct);await Task.WhenAll(stdout,stderr);}
   finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync(CancellationToken.None);}}
   if(process.ExitCode!=0)throw new InvalidOperationException("Native Proxmox snapshot export failed");
   var archives=Directory.GetFiles(staging).Where(p=>ArchiveGuest(p) is not null).ToArray();
   if(archives.Length!=source.GuestIds.Length||archives.Select(p=>ArchiveGuest(p)!.Value.Id).Order().SequenceEqual(source.GuestIds.Order())==false)
    throw new InvalidOperationException("Native export did not produce exactly the selected guest archives");
   foreach(var archive in archives)
   {
    UnixPrivatePaths.TrustedFile(archive);
    using var input=File.OpenRead(archive);var magic=new byte[4];
    if(input.Length<24||input.Read(magic)!=4||!magic.AsSpan().SequenceEqual(new byte[]{0x28,0xb5,0x2f,0xfd}))throw new InvalidOperationException("Native export archive is incomplete");
    File.SetUnixFileMode(archive,UnixFileMode.UserRead|UnixFileMode.UserWrite);
   }
   // Logs and notes may contain guest metadata; only full native image archives enter this backup.
   foreach(var file in Directory.GetFiles(staging).Except(archives))File.Delete(file);
   if(Directory.EnumerateDirectories(staging).Any())throw new InvalidOperationException("Unexpected directory in native export");
   var previous=Path.Combine(parent,"latest.previous");if(Directory.Exists(previous)){UnixPrivatePaths.Directory(previous,false);Directory.Delete(previous,true);}
   Directory.Move(latest,previous);
   try{Directory.Move(staging,latest);}catch{Directory.Move(previous,latest);throw;}
   Directory.Delete(previous,true);
  }
  finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
 }
 private static async Task Discard(StreamReader reader,CancellationToken ct)
 {
  var buffer=new char[8192];while(await reader.ReadAsync(buffer,ct)>0){} // Bounded memory; never forward native export output.
 }
 public static (int Id,string Type)? ArchiveGuest(string path)
 {
  var name=Path.GetFileName(path);
  var match=Regex.Match(name,@"^vzdump-(qemu|lxc)-([1-9][0-9]{2,8})-[0-9]{4}_[0-9]{2}_[0-9]{2}-[0-9]{2}_[0-9]{2}_[0-9]{2}\.(vma|tar)\.zst$",RegexOptions.CultureInvariant);
  if(!match.Success||match.Groups[1].Value=="qemu"&&match.Groups[3].Value!="vma"||match.Groups[1].Value=="lxc"&&match.Groups[3].Value!="tar")return null;
  return(int.Parse(match.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture),match.Groups[1].Value);
 }
}
