using System.Security.AccessControl;
using System.Security.Principal;
namespace DariaTech.Agent;

// Local administrator configuration only; never accepted from managed job definitions.
// Validates the entire installation because the executable loads adjacent assemblies.
public static class EngineInstallation
{
 public static string Resolve(AgentOptions options)
 {
  if(!string.IsNullOrWhiteSpace(options.ExternalEngineExecutable))
  {
   ValidateExternal(options.ExternalEngineExecutable);
   return Path.GetFullPath(options.ExternalEngineExecutable);
  }
  var bundled=Path.Combine(AppContext.BaseDirectory,"engine","Duplicati.Server.exe");
  if(!File.Exists(bundled))throw new InvalidOperationException("Bundled engine missing");
  return bundled;
 }
 public static void ValidateExternal(string executable)
 {
  if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("External managed engine currently requires Windows");
  if(!Path.IsPathFullyQualified(executable)||executable.StartsWith(@"\\")||
     !string.Equals(Path.GetFileName(executable),"Duplicati.Server.exe",StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("External engine requires a local absolute Duplicati.Server.exe path");
  var path=Path.GetFullPath(executable);
  if(!File.Exists(path))throw new InvalidOperationException("External engine missing");
  var trusted=new HashSet<string>{"S-1-5-18","S-1-5-32-544",
   ((SecurityIdentifier)new NTAccount("NT SERVICE","TrustedInstaller").Translate(typeof(SecurityIdentifier))).Value};
  var root=new DirectoryInfo(Path.GetDirectoryName(path)!);
  for(var parent=root.Parent;parent is not null;parent=parent.Parent)Check(parent,false,trusted);
  var pending=new Stack<DirectoryInfo>();pending.Push(root);
  var entries=0;
  while(pending.TryPop(out var directory))
  {
   Check(directory,true,trusted);
   foreach(var entry in directory.EnumerateFileSystemInfos())
   {
    if(++entries>50000)throw new InvalidOperationException("External engine installation exceeds validation limit");
    Check(entry,true,trusted);
    if(entry is DirectoryInfo child)pending.Push(child);
   }
  }
 }
 private static void Check(FileSystemInfo entry,bool installation,HashSet<string> trusted)
 {
  if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Engine installation cannot contain links or junctions");
  FileSystemSecurity acl=entry is DirectoryInfo directory?directory.GetAccessControl():((FileInfo)entry).GetAccessControl();
  if(acl.GetOwner(typeof(SecurityIdentifier))?.Value is not {} owner||!trusted.Contains(owner))
   throw new InvalidOperationException("Engine installation must have privileged owners");
  var dangerous=FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.Delete;
  if(installation)dangerous|=FileSystemRights.WriteData|FileSystemRights.AppendData|FileSystemRights.WriteAttributes|FileSystemRights.WriteExtendedAttributes;
  foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
   if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&
      !trusted.Contains(rule.IdentityReference.Value)&&(rule.FileSystemRights&dangerous)!=0)
    throw new InvalidOperationException("Engine installation can be modified by an unprivileged principal");
 }
}
