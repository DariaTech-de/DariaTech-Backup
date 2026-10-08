using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public static class UnixUpdatePackage
{
 public static async Task<string> Extract(string archive,string destination,AgentUpdateManifest manifest,CancellationToken ct)
 {
  if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
  if(Directory.Exists(destination)||File.Exists(destination))throw new InvalidOperationException("Update extraction requires a new private directory");
  UnixPrivatePaths.Directory(destination,true);
  await using var file=File.OpenRead(archive);await using var gzip=new GZipStream(file,CompressionMode.Decompress);
  using var reader=new TarReader(gzip,leaveOpen:true);var count=0;long total=0;
  while(await reader.GetNextEntryAsync(copyData:false,cancellationToken:ct) is {} entry)
  {
   if(++count>50000||entry.Length<0||entry.Length>536870912||(total+=entry.Length)>2147483648)throw new InvalidOperationException("Update archive exceeds extraction bounds");
   if(entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile)||!string.IsNullOrEmpty(entry.LinkName))throw new InvalidOperationException("Update archive links and special entries are forbidden");
   var name=entry.Name.TrimEnd('/');
   if(string.IsNullOrWhiteSpace(name)||name.Length>1000||name[0]=='/'||name.Contains('\\')||name.Any(char.IsControl)||name.Split('/').Any(p=>p is "" or "." or ".."))throw new InvalidOperationException("Unsafe update archive path");
   var parts=name.Split('/');if(parts[0]!="agent"&&!(parts.Length==1&&parts[0] is "platform.txt" or "version.txt" or "install.sh" or "uninstall.sh"))throw new InvalidOperationException("Unexpected update archive root");
   var target=Path.GetFullPath(Path.Combine(destination,name));
   if(!target.StartsWith(Path.GetFullPath(destination)+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new InvalidOperationException("Archive traversal rejected");
   if(entry.EntryType==TarEntryType.Directory){Directory.CreateDirectory(target,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);continue;}
   Directory.CreateDirectory(Path.GetDirectoryName(target)!);
   var executable=parts[^1] is "DariaTech.Agent" or "Duplicati.Server";
   await using var output=new FileStream(target,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=executable?UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute:UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.OtherRead});
   if(entry.DataStream is not null)await entry.DataStream.CopyToAsync(output,ct);
   if(output.Length!=entry.Length)throw new InvalidOperationException("Truncated update archive entry");
   await output.FlushAsync(ct);output.Flush(true);
  }
  var agent=Path.Combine(destination,"agent");
  if(File.ReadAllText(Path.Combine(destination,"platform.txt")).Trim()!=manifest.Platform||File.ReadAllText(Path.Combine(destination,"version.txt")).Trim()!=manifest.Version||
     AssemblyName.GetAssemblyName(Path.Combine(agent,"DariaTech.Agent.dll")).Version?.ToString()!=manifest.Version)throw new InvalidOperationException("Native platform or compiled agent version differs from signed manifest");
  UnixPrivatePaths.TrustedFile(Path.Combine(agent,"DariaTech.Agent"));
  UnixPrivatePaths.Installation(Path.Combine(agent,"engine","Duplicati.Server"));
  return agent;
 }
}
