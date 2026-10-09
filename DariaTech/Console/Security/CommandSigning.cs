using System.Security.Cryptography;
using DariaTech.Contracts;
namespace DariaTech.Console.Security;
// Signs device commands (restore, verify, folder browse, connection test). Agents pin the public key at
// installation, so the Console works out of the box: without a configured key file it creates its own P-256
// key once, stored encrypted with the master key in the persistent key directory.
public sealed class CommandSigning(IConfiguration configuration,ISecretStore secrets)
{
 const string Purpose="command-signing-key";
 readonly Lock gate=new();string? pem;
 public bool Enabled=>true;
 public SignedCommand Sign(DeviceCommand command){using var key=Load();return CommandProtocol.Sign(command,key);}
 public string PublicKeyPem{get{using var key=Load();return key.ExportSubjectPublicKeyInfoPem();}}
 ECDsa Load()
 {
  var key=ECDsa.Create();key.ImportFromPem(Pem());
  if(key.KeySize!=256){key.Dispose();throw new CryptographicException("Commands require ECDSA P-256");}
  return key;
 }
 string Pem()
 {
  lock(gate)
  {
   if(pem is not null)return pem;
   if(configuration["Commands:SigningKeyFile"] is {Length:>0} file)return pem=File.ReadAllText(file);
   var directory=configuration["Security:KeyDirectory"]??"./keys";Directory.CreateDirectory(directory);
   var path=Path.Combine(directory,"command-signing.key");
   if(!File.Exists(path))
   {
    using var created=ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var protectedKey=secrets.Protect(Guid.Empty,Purpose,created.ExportPkcs8PrivateKeyPem());
    // Written completely before it appears; a concurrently starting instance keeps the key that came first.
    var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temp,protectedKey);
    try{File.Move(temp,path,false);}catch(IOException)when(File.Exists(path)){File.Delete(temp);}
   }
   return pem=secrets.Unprotect(Guid.Empty,Purpose,File.ReadAllText(path));
  }
 }
}
