using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed record EngineTlsIdentity(string Pfx,string Password,string Certificate,DateTimeOffset Expires);
public sealed record EngineTlsFile(string Path,string Password);
public static class EngineTls
{
 public static EngineTlsFile Provision(AgentOptions options,ProtectedState state)
 {
  if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Unix managed-engine TLS only");
  var material=state.Read<EngineTlsIdentity>("engine-tls.bin");
  if(material is null||material.Expires<DateTimeOffset.UtcNow.AddDays(30))
  {
   using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
   var request=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256);
   var names=new SubjectAlternativeNameBuilder();names.AddDnsName("localhost");names.AddIpAddress(System.Net.IPAddress.Loopback);
   request.CertificateExtensions.Add(names.Build());request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
   request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
   request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
   using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2));
   var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
   material=new(Convert.ToBase64String(certificate.Export(X509ContentType.Pfx,password)),password,Convert.ToBase64String(certificate.Export(X509ContentType.Cert)),certificate.NotAfter.ToUniversalTime());
   state.Write("engine-tls.bin",material);
  }
  var path=Path.Combine(options.StateDirectory,"engine-cert.pfx");
  if(File.Exists(path))UnixPrivatePaths.File(path);
  var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  using(var file=new FileStream(temporary,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite})){file.Write(Convert.FromBase64String(material.Pfx));file.Flush(true);}
  File.Move(temporary,path,true);return new(path,material.Password);
 }
 public static HttpMessageHandler Handler(ProtectedState state)=>new SocketsHttpHandler
 {
  AllowAutoRedirect=false,UseProxy=false,
  SslOptions=new System.Net.Security.SslClientAuthenticationOptions
  {
   RemoteCertificateValidationCallback=(_,certificate,_,errors)=>
   {
    if(certificate is null||(errors&(SslPolicyErrors.RemoteCertificateNameMismatch|SslPolicyErrors.RemoteCertificateNotAvailable))!=0)return false;
    var material=state.Read<EngineTlsIdentity>("engine-tls.bin");if(material is null||material.Expires<=DateTimeOffset.UtcNow)return false;
    return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()),SHA256.HashData(Convert.FromBase64String(material.Certificate)));
   }
  }
 };
}
