using DariaTech.Agent;
using DariaTech.Contracts;
using NUnit.Framework;
namespace DariaTech.Tests;

public sealed class SourceTests
{
 private static ManagedBackupDefinition Files()=>new("NAS",["/mnt/share/data"],"file:///backup/","test-only-passphrase",new(),30,[],null);
 [Test]public void TenantSourcesRejectMissingMountsWrongServersNestedMountsAndSubdirectoryBinds()
 {
  if(OperatingSystem.IsWindows()){Assert.Ignore("Unix mount namespaces");return;}
  var requirements=new[]{new SourceMountRequirement("/mnt/share","nfs4","nas:/company")};
  Assert.Throws<InvalidOperationException>(()=>SourceChecks.ValidateMounts(["/mnt/share/data"],requirements,[]));
  Assert.Throws<InvalidOperationException>(()=>SourceChecks.ValidateMounts(["/mnt/share/data"],requirements,[new("/mnt/share","nfs4","other:/company")]));
  Assert.Throws<InvalidOperationException>(()=>SourceChecks.ValidateMounts(["/mnt/share"],requirements,[new("/mnt/share","nfs4","nas:/company"),new("/mnt/share/foreign","nfs4","nas:/other")]));
  Assert.DoesNotThrow(()=>SourceChecks.ValidateMounts(["/mnt/share/data"],requirements,[new("/mnt/share","nfs4","nas:/company")]));
  var parsed=SourceChecks.ParseLinuxMounts(["31 20 0:43 / /mnt/my\\040share rw - nfs4 nas:/company rw","32 20 0:43 /other /mnt/share rw - nfs4 nas:/company rw"]);
  Assert.That(parsed,Is.EqualTo(new[]{new MountedSource("/mnt/my share","nfs4","nas:/company")}));
  Assert.That(SourceChecks.ParseMacMounts(["nas:/company on /Volumes/My Data (nfs, nodev, nosuid)"]),Is.EqualTo(new[]{new MountedSource("/Volumes/My Data","nfs","nas:/company")}));
 }
 [Test]public void ProxmoxGuestSelectionDoesNotAcceptArbitraryCommandsOrMixedCloudSources()
 {
  var definition=Files() with{Sources=[],Proxmox=new([101,102])};
  Assert.That(ConfigurationPolicy.Valid(definition),Is.True);
  Assert.That(ConfigurationPolicy.Valid(definition with{Sources=["/etc"]}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(definition with{Proxmox=new([101,101])}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(definition with{Proxmox=new([1])}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(definition with{Saas=SaasTests.Office()}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(Files() with{SourceMounts=[new("/mnt/share","cifs","//user:password@nas/share")]}),Is.False);
  Assert.That(ConfigurationPolicy.Valid(Files() with{SourceMounts=[new("/mnt/share","smbfs","//user@nas/share")]}),Is.True);
  Assert.That(ConfigurationPolicy.Valid(Files() with{Sources=[@"\\nas\company\Documents"]}),Is.True);
  Assert.That(ProxmoxWorkloads.ArchiveGuest("vzdump-qemu-101-2026_10_08-08_00_00.vma.zst"),Is.EqualTo((101,"qemu")));
  Assert.That(ProxmoxWorkloads.ArchiveGuest("vzdump-lxc-102-2026_10_08-08_00_00.tar.zst"),Is.EqualTo((102,"lxc")));
  Assert.That(ProxmoxWorkloads.ArchiveGuest("vzdump-qemu-101-2026_10_08-08_00_00.tar.zst"),Is.Null);
 }
 [Test]public void ImageImportSelectionCannotEnableOverwriteOrInjectAStorageCommand()
 {
  var now=DateTimeOffset.UtcNow;var selection=new ProxmoxRestoreSelection(1,now.AddDays(-1),"/staging/latest/vzdump-qemu-101-2026_10_07-08_00_00.vma.zst",901,"customer-storage",true);
  var command=new DeviceCommand(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"1",RemoteAction.RestoreProxmox,null,now,now.AddMinutes(10),ProxmoxRestore:selection);
  Assert.That(CommandProtocol.Valid(command,command.DeviceId,now),Is.True);
  Assert.That(CommandProtocol.Valid(command with{ProxmoxRestore=selection with{ConfirmImport=false}},command.DeviceId,now),Is.False);
  Assert.That(CommandProtocol.Valid(command with{ProxmoxRestore=selection with{Storage="local; reboot"}},command.DeviceId,now),Is.False);
  Assert.That(CommandProtocol.Valid(command with{Action=RemoteAction.RunBackup},command.DeviceId,now),Is.False);
  var binding=new ManagedSourceBinding(Guid.NewGuid(),1,["/staging/latest/"],[],new([101]));
  Assert.Throws<InvalidOperationException>(()=>ProxmoxRestore.Authorize(new AgentOptions(),binding,selection));
 }
 [Test]public void ProxmoxHostFileJobsCannotCrossLocallyAssignedCustomerBoundary()
 {
  var options=new AgentOptions{AllowProxmoxSnapshots=true,AllowedFileSourceRoots=["/customer/one"]};
  Assert.DoesNotThrow(()=>SourceChecks.AuthorizeFiles(options,["/customer/one/data"]));
  Assert.Throws<InvalidOperationException>(()=>SourceChecks.AuthorizeFiles(options,["/customer/two/data"]));
  Assert.Throws<InvalidOperationException>(()=>SourceChecks.AuthorizeFiles(options,["/customer/one/../two"]));
  Assert.Throws<InvalidOperationException>(()=>SourceChecks.AuthorizeFiles(options,["/customer/one-other"]));
  Assert.Throws<InvalidOperationException>(()=>ProxmoxWorkloads.Authorize(options,Guid.NewGuid(),new([101])));
 }
}
