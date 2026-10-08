using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
namespace DariaTech.Agent;

public static class UnixPrivatePaths
{
 [DllImport("libc",EntryPoint="geteuid")]private static extern uint GetEffectiveUserId();
 public static uint EffectiveUserId=>OperatingSystem.IsWindows()?throw new PlatformNotSupportedException():GetEffectiveUserId();
 public static void Directory(string path,bool create)
 {
  if(OperatingSystem.IsWindows())return;
  path=Path.GetFullPath(path);
  var parent=new DirectoryInfo(path);
  while(!parent.Exists&&parent.Parent is not null)parent=parent.Parent;
  Ancestors(parent);
  if(!System.IO.Directory.Exists(path))
  {
   if(!create)throw new InvalidOperationException("Private directory must be provisioned locally");
   System.IO.Directory.CreateDirectory(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
  }
  Check(new DirectoryInfo(path),true,false);
 }
 public static void File(string path)
 {
  if(OperatingSystem.IsWindows())return;
  var file=new FileInfo(Path.GetFullPath(path));
  if(!file.Exists)throw new InvalidOperationException("Protected local file missing");
  Ancestors(file.Directory!);Check(file,true,true);
 }
 public static void TrustedDirectory(string path)
 {
  if(OperatingSystem.IsWindows())return;
  var directory=new DirectoryInfo(Path.GetFullPath(path));if(!directory.Exists)throw new InvalidOperationException("Trusted directory missing");
  Ancestors(directory);Check(directory,false,false);
 }
 public static void TrustedFile(string path)
 {
  if(OperatingSystem.IsWindows())return;
  var file=new FileInfo(Path.GetFullPath(path));if(!file.Exists)throw new InvalidOperationException("Trusted file missing");
  Ancestors(file.Directory!);Check(file,false,true);
 }
 public static void Installation(string executable)
 {
  if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
  if(!Path.IsPathFullyQualified(executable)||!System.IO.File.Exists(executable)||Path.GetFileName(executable) is not ("Duplicati.Server" or "duplicati-server"))throw new InvalidOperationException("Absolute native engine executable required");
  var root=new DirectoryInfo(Path.GetDirectoryName(executable)!);Ancestors(root);
  var directories=new Stack<DirectoryInfo>();directories.Push(root);var count=0;var ownership=new List<string>();
  while(directories.TryPop(out var directory))
  {
   Check(directory,false,false,false);ownership.Add(directory.FullName);
   foreach(var item in directory.EnumerateFileSystemInfos())
   {
    if(++count>50000)throw new InvalidOperationException("Engine installation exceeds validation limit");
    Check(item,false,item is FileInfo,false);ownership.Add(item.FullName);if(item is DirectoryInfo child)directories.Push(child);
   }
  }
  Owners(ownership);
  if((System.IO.File.GetUnixFileMode(executable)&UnixFileMode.UserExecute)==0)throw new InvalidOperationException("Engine executable permission missing");
 }
 private static void Ancestors(DirectoryInfo directory)
 {
  if(OperatingSystem.IsWindows())return;
  var ownership=new List<string>();
  for(DirectoryInfo? parent=directory;parent is not null;parent=parent.Parent)
  {
   if(parent.LinkTarget is not null)throw new InvalidOperationException("Trusted paths cannot contain links");
   ownership.Add(parent.FullName);
   var mode=System.IO.File.GetUnixFileMode(parent.FullName);
   if((mode&(UnixFileMode.OtherWrite|UnixFileMode.GroupWrite))!=0&&(mode&UnixFileMode.StickyBit)==0)throw new InvalidOperationException("Trusted path ancestry is writable by other principals");
  }
  Owners(ownership);
 }
 private static void Check(FileSystemInfo entry,bool secret,bool file,bool owner=true)
 {
  if(OperatingSystem.IsWindows())return;
  if(entry.LinkTarget is not null||(entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Trusted paths cannot contain links");
  if(owner)Owners([entry.FullName]);var mode=System.IO.File.GetUnixFileMode(entry.FullName);
  var forbidden=UnixFileMode.GroupWrite|UnixFileMode.OtherWrite|UnixFileMode.SetUser|UnixFileMode.SetGroup;
  if(secret)forbidden|=UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute;
  if((mode&forbidden)!=0||secret&&file&&(mode&UnixFileMode.UserExecute)!=0)throw new InvalidOperationException("Unsafe private path permissions");
 }
 private static void Owners(IEnumerable<string> paths)
 {
  if(OperatingSystem.IsWindows())return;
  foreach(var batch in paths.Distinct(StringComparer.Ordinal).Chunk(128))
  {
   var start=new ProcessStartInfo("/usr/bin/stat"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
   start.ArgumentList.Add(OperatingSystem.IsMacOS()?"-f":"-c");start.ArgumentList.Add("%u");foreach(var path in batch)start.ArgumentList.Add(path);
   using var process=Process.Start(start)??throw new InvalidOperationException("Path ownership inspection unavailable");
   if(!process.WaitForExit(5000)){process.Kill();throw new InvalidOperationException("Path ownership inspection timed out");}
   var values=process.StandardOutput.ReadToEnd().Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries);
   if(process.ExitCode!=0||values.Length!=batch.Length||values.Any(value=>!uint.TryParse(value.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var id)||id!=0&&id!=EffectiveUserId))
    throw new InvalidOperationException("Trusted paths require root or service-identity ownership");
  }
 }
}
