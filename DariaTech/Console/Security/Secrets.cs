using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
namespace DariaTech.Console.Security;

public interface ISecretStore
{
 string Protect(Guid tenant, string purpose, string value);
 string Unprotect(Guid tenant, string purpose, string value);
}
public sealed class EncryptedSecretStore : ISecretStore
{
 private readonly byte[] key;
 public EncryptedSecretStore(IConfiguration config)
 {
  var file=config["Security:MasterKeyFile"] ?? throw new InvalidOperationException("Security:MasterKeyFile is required");
  key=Convert.FromBase64String(File.ReadAllText(file).Trim());
  if(key.Length!=32) throw new InvalidOperationException("Master key must be 256 bits");
 }
 public string Protect(Guid tenant,string purpose,string value)
 {
  byte[] nonce=RandomNumberGenerator.GetBytes(12), plain=Encoding.UTF8.GetBytes(value), encrypted=new byte[plain.Length], tag=new byte[16];
  using var aes=new AesGcm(key,16);
  aes.Encrypt(nonce,plain,encrypted,tag,Encoding.UTF8.GetBytes($"v1:{tenant}:{purpose}"));
  return Convert.ToBase64String(nonce.Concat(tag).Concat(encrypted).ToArray());
 }
 public string Unprotect(Guid tenant,string purpose,string value)
 {
  var data=Convert.FromBase64String(value);
  if(data.Length<28) throw new CryptographicException("Invalid envelope");
  var plain=new byte[data.Length-28]; using var aes=new AesGcm(key,16);
  aes.Decrypt(data.AsSpan(0,12),data.AsSpan(28),data.AsSpan(12,16),plain,Encoding.UTF8.GetBytes($"v1:{tenant}:{purpose}"));
  return Encoding.UTF8.GetString(plain);
 }
}
public sealed class KeyXmlEncryptor(ISecretStore secrets) : IXmlEncryptor
{
 public EncryptedXmlInfo Encrypt(XElement element) => new(new XElement("encrypted",secrets.Protect(Guid.Empty,"data-protection",element.ToString(SaveOptions.DisableFormatting))),typeof(KeyXmlDecryptor));
}
public sealed class KeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
 public XElement Decrypt(XElement element)=>XElement.Parse(services.GetRequiredService<ISecretStore>().Unprotect(Guid.Empty,"data-protection",element.Value));
}
public static class Tokens
{
 public static string Create()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
 public static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
