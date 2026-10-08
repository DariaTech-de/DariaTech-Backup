using System.Security.AccessControl;
using System.Security.Principal;
namespace DariaTech.Agent;

public static class RestoreRootSecurity
{
 public static void Validate(string root)
 {
  if(!OperatingSystem.IsWindows()){UnixPrivatePaths.Directory(root,false);return;}
  var trusted=new HashSet<string>{"S-1-5-18","S-1-5-32-544",((SecurityIdentifier)new NTAccount("NT SERVICE","TrustedInstaller").Translate(typeof(SecurityIdentifier))).Value};
  var first=true;
  for(DirectoryInfo? directory=new(root);directory is not null;directory=directory.Parent)
  {
   var acl=directory.GetAccessControl();
   if(acl.GetOwner(typeof(SecurityIdentifier))?.Value is not {} owner||!trusted.Contains(owner))throw new InvalidOperationException("Restore root ancestry must have privileged owners");
   if(first&&!acl.AreAccessRulesProtected)throw new InvalidOperationException("Restore root requires a protected local ACL");
   foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
   {
    if(rule.AccessControlType!=AccessControlType.Allow||(rule.PropagationFlags&PropagationFlags.InheritOnly)!=0||trusted.Contains(rule.IdentityReference.Value))continue;
    var dangerous=FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
    if(first||(rule.FileSystemRights&dangerous)!=0)throw new InvalidOperationException("Restore root cannot be exposed or replaced by unprivileged users");
   }
   first=false;
  }
 }
}
