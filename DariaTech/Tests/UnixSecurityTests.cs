using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DariaTech.Agent;
using NUnit.Framework;
namespace DariaTech.Tests;

public sealed class UnixSecurityTests
{
 internal static string TemporaryDirectory()
 {
  var root=Path.GetTempPath();if(OperatingSystem.IsMacOS()&&root.StartsWith("/var/",StringComparison.Ordinal))root="/private"+root;
  var path=Path.Combine(root,"dt-unix-"+Guid.NewGuid());UnixPrivatePaths.Directory(path,true);return path;
 }
 internal static AgentOptions Options(string root)
 {
  var options=new AgentOptions{StateDirectory=Path.Combine(root,"state"),LinuxKeyFile=Path.Combine(root,"key")};
  UnixProvisioning.WritePrivate(options.LinuxKeyFile,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));return options;
 }
 [Test]public void PrivateStateRejectsReadableKeysAndLinksAndAuthenticatesFilenames()
 {
  if(OperatingSystem.IsWindows()){Assert.Ignore("Unix permissions; Windows service ACL and DPAPI tested separately");return;}
  var root=TemporaryDirectory();
  try
  {
   var options=Options(root);var state=new ProtectedState(options);state.Write("identity.bin","test-only-secret");
   Assert.That(File.GetUnixFileMode(Path.Combine(options.StateDirectory,"identity.bin")),Is.EqualTo(UnixFileMode.UserRead|UnixFileMode.UserWrite));
   Assert.That(File.ReadAllText(Path.Combine(options.StateDirectory,"identity.bin")),Does.Not.Contain("test-only-secret"));
   File.Copy(Path.Combine(options.StateDirectory,"identity.bin"),Path.Combine(options.StateDirectory,"other.bin"));
   Assert.Throws<AuthenticationTagMismatchException>(()=>state.Read<string>("other.bin"));
   Assert.Throws<InvalidOperationException>(()=>state.Write("../outside.bin","blocked"));
   File.SetUnixFileMode(options.LinuxKeyFile!,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.OtherRead);
   Assert.Throws<InvalidOperationException>(()=>state.Read<string>("identity.bin"));
   File.SetUnixFileMode(options.LinuxKeyFile!,UnixFileMode.UserRead|UnixFileMode.UserWrite);
   File.CreateSymbolicLink(Path.Combine(options.StateDirectory,"linked.bin"),Path.Combine(options.StateDirectory,"identity.bin"));
   Assert.Throws<InvalidOperationException>(()=>state.Read<string>("linked.bin"));
   File.Delete(Path.Combine(options.StateDirectory,"linked.bin"));
   File.SetUnixFileMode(options.StateDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.OtherWrite);
   Assert.Throws<InvalidOperationException>(()=>state.Write("identity.bin","blocked"));
  }finally{Directory.Delete(root,true);}
 }
 [Test]public void EngineInstallationRejectsWritableAdjacentAssemblies()
 {
  if(OperatingSystem.IsWindows()){Assert.Ignore("Unix installation permissions");return;}
  var root=TemporaryDirectory();
  try
  {
   var file=Path.Combine(root,"Duplicati.Server");File.WriteAllText(file,"test-only-never-executed");File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
   var adjacent=Path.Combine(root,"test.dll");File.WriteAllText(adjacent,"test");File.SetUnixFileMode(adjacent,UnixFileMode.UserRead|UnixFileMode.UserWrite);
   EngineInstallation.ValidateExternal(file);
   File.SetUnixFileMode(adjacent,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.OtherWrite);
   Assert.Throws<InvalidOperationException>(()=>EngineInstallation.ValidateExternal(file));
  }finally{Directory.Delete(root,true);}
 }
 [Test]public async Task ManagedEngineTlsRejectsForeignLoopbackCertificateBeforeHttpCredentials()
 {
  if(OperatingSystem.IsWindows()){Assert.Ignore("Unix pinned TLS; Windows process-bound transport tested separately");return;}
  var root=TemporaryDirectory();
  try
  {
   var options=Options(root);var state=new ProtectedState(options);var tls=EngineTls.Provision(options,state);
   var material=state.Read<EngineTlsIdentity>("engine-tls.bin")!;
   using var certificate=X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(material.Pfx),material.Password);
   using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
   var port=((IPEndPoint)listener.LocalEndpoint).Port;
   var trusted=Serve(listener,certificate);
   using(var client=new HttpClient(EngineTls.Handler(state))){Assert.That(await client.GetStringAsync($"https://127.0.0.1:{port}"),Is.EqualTo("{}"));}
   Assert.That(await trusted,Is.True);
   using var foreignKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=new CertificateRequest("CN=localhost",foreignKey,HashAlgorithmName.SHA256);
   var names=new SubjectAlternativeNameBuilder();names.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(names.Build());
   using var foreign=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
   var rogue=Serve(listener,foreign);
   using(var client=new HttpClient(EngineTls.Handler(state))){Assert.ThrowsAsync<HttpRequestException>(async()=>await client.GetStringAsync($"https://127.0.0.1:{port}"));}
   Assert.That(await rogue,Is.False,"No HTTP request may reach an unpinned local listener");
   var start=ManagedEngine.CreateStartInfo("/opt/engine/Duplicati.Server",options.StateDirectory,"https://127.0.0.1:"+port,"test-engine-password","test-db-key",tls);
   Assert.That(string.Join(" ",start.ArgumentList),Does.Not.Contain(tls.Password).And.Not.Contain("test-engine-password").And.Not.Contain("test-db-key"));
   Assert.That(start.Environment["DUPLICATI__WEBSERVICE_SSLCERTIFICATEPASSWORD"],Is.EqualTo(tls.Password));
  }finally{Directory.Delete(root,true);}
 }
 private static async Task<bool> Serve(TcpListener listener,X509Certificate2 certificate)
 {
  using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));using var socket=await listener.AcceptTcpClientAsync(timeout.Token);
  await using var stream=new SslStream(socket.GetStream(),false);
  try
  {
   await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate},timeout.Token);
   var bytes=new byte[1024];var count=await stream.ReadAsync(bytes,timeout.Token);if(count<1)return false;
   await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"),timeout.Token);return true;
  }catch(Exception error)when(error is System.Security.Authentication.AuthenticationException or IOException){return false;}
 }
}
