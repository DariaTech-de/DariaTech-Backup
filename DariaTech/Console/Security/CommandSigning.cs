using System.Security.Cryptography;
using DariaTech.Contracts;
namespace DariaTech.Console.Security;
public sealed class CommandSigning(IConfiguration configuration)
{
 public bool Enabled=>!string.IsNullOrWhiteSpace(configuration["Commands:SigningKeyFile"]);
 public SignedCommand Sign(DeviceCommand command)
 {
  using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(configuration["Commands:SigningKeyFile"]??throw new InvalidOperationException("Command signing key required")));
  if(key.KeySize!=256)throw new CryptographicException("Commands require ECDSA P-256");return CommandProtocol.Sign(command,key);
 }
}
