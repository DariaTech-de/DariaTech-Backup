using System.Formats.Tar;
using System.IO.Compression;
using DariaTech.Agent;
using DariaTech.Contracts;
using NUnit.Framework;
namespace DariaTech.Tests;

public sealed class UnixUpdateTests
{
 [TestCase("../outside",TarEntryType.RegularFile)]
 [TestCase("/etc/should-not-exist",TarEntryType.RegularFile)]
 [TestCase("agent/linked",TarEntryType.SymbolicLink)]
 [TestCase("agent/hardlinked",TarEntryType.HardLink)]
 [TestCase("agent/device",TarEntryType.CharacterDevice)]
 public async Task NativeUpdatesRejectArchiveTraversalLinksAndDevices(string name,TarEntryType type)
 {
  if(OperatingSystem.IsWindows()){Assert.Ignore("Unix archive policy");return;}
  var root=UnixSecurityTests.TemporaryDirectory();
  try
  {
   var archive=Path.Combine(root,"test.tar.gz");
   await using(var file=File.Create(archive))
   await using(var gzip=new GZipStream(file,CompressionMode.Compress))
   await using(var writer=new TarWriter(gzip,TarEntryFormat.Pax,leaveOpen:true))
   {
    var entry=new PaxTarEntry(type,name);
    if(type is TarEntryType.SymbolicLink or TarEntryType.HardLink)entry.LinkName="/etc/passwd";
    if(type==TarEntryType.RegularFile)entry.DataStream=new MemoryStream([1,2,3]);
    await writer.WriteEntryAsync(entry);
   }
   var manifest=new AgentUpdateManifest(Guid.NewGuid(),1,"DariaTechBackupAgent",new AgentOptions().Platform,"0.3.0.0","https://github.com/test/package.tar.gz",new string('A',64),2048,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(1));
   Assert.ThrowsAsync<InvalidOperationException>(async()=>await UnixUpdatePackage.Extract(archive,Path.Combine(root,"extracted"),manifest,CancellationToken.None));
   Assert.That(File.Exists(Path.Combine(root,"outside")),Is.False);
  }finally{Directory.Delete(root,true);}
 }
 [Test]public void PrivateConfigurationRetainsAgentSectionAndWritableOptions()
 {
  var options=new AgentOptions{LinuxKeyFile="/var/lib/test/key",StateDirectory="/var/lib/test",ManageEngine=true};
  using var config=System.Text.Json.JsonDocument.Parse(UnixProvisioning.Serialize(options));
  var settings=config.RootElement.GetProperty("Agent");Assert.That(settings.GetProperty("LinuxKeyFile").GetString(),Is.EqualTo(options.LinuxKeyFile));
  Assert.That(settings.GetProperty("ManageEngine").GetBoolean(),Is.True);
  Assert.That(settings.TryGetProperty("Version",out _),Is.False);Assert.That(settings.TryGetProperty("Platform",out _),Is.False);
 }
 [Test]public void UnixManagedTransportAndUnsupportedArchitecturesAreExplicit()
 {
  if(OperatingSystem.IsWindows()){Assert.Ignore("Unix loopback policy");return;}
  var root=UnixSecurityTests.TemporaryDirectory();
  try
  {
   var options=UnixSecurityTests.Options(root);options.ManageEngine=true;
   Assert.DoesNotThrow(options.Validate);
   options.EngineUrl="http://127.0.0.1:8210";Assert.Throws<InvalidOperationException>(options.Validate);
   options.EngineUrl="https://127.0.0.1:8210";options.AllowAgentUpdates=true;
   Assert.Throws<InvalidOperationException>(options.Validate);
  }finally{Directory.Delete(root,true);}
 }
}
