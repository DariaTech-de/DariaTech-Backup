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
public sealed class EncryptedSecretStore : ISecretStore,IDisposable
{
 private readonly byte[] key;
 private readonly Dictionary<string,byte[]> keys=new(StringComparer.Ordinal);
 private readonly string keyId;
 public EncryptedSecretStore(IConfiguration config)
 {
  key=ReadKey(config["Security:MasterKeyFile"]??throw new InvalidOperationException("Security:MasterKeyFile is required"));keyId=Id(key);keys.Add(keyId,key);
  var legacy=(config["Security:LegacyMasterKeyFiles"]??"").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
  if(legacy.Length>16)throw new InvalidOperationException("At most 16 retained master keys allowed");
  foreach(var file in legacy){var old=ReadKey(file);if(!keys.TryAdd(Id(old),old))CryptographicOperations.ZeroMemory(old);}
 }
 private static byte[] ReadKey(string file)
 {var value=Convert.FromBase64String(File.ReadAllText(file).Trim());if(value.Length!=32)throw new InvalidOperationException("Master key must be 256 bits");return value;}
 private static string Id(byte[] value)=>Convert.ToHexString(SHA256.HashData(value).AsSpan(0,16));
 public string Protect(Guid tenant,string purpose,string value)
 {
  byte[] nonce=RandomNumberGenerator.GetBytes(12),plain=Encoding.UTF8.GetBytes(value),encrypted=new byte[plain.Length],tag=new byte[16];
  try{using var aes=new AesGcm(key,16);aes.Encrypt(nonce,plain,encrypted,tag,Encoding.UTF8.GetBytes($"v1:{tenant}:{purpose}"));return $"v2:{keyId}:"+Convert.ToBase64String(nonce.Concat(tag).Concat(encrypted).ToArray());}
  finally{CryptographicOperations.ZeroMemory(plain);}
 }
 public string Unprotect(Guid tenant,string purpose,string value)
 {
  if(value.StartsWith("v2:",StringComparison.Ordinal))
  {
   var parts=value.Split(':');if(parts.Length!=3||!keys.TryGetValue(parts[1],out var selected))throw new CryptographicException("Unknown secret key");
   return Decrypt(selected,tenant,purpose,parts[2]);
  }
  // Original envelopes have no key identifier. Retained keys are tried only with authenticated AES-GCM.
  foreach(var selected in keys.Values)
  {try{return Decrypt(selected,tenant,purpose,value);}catch(AuthenticationTagMismatchException){}}
  throw new AuthenticationTagMismatchException("No retained key authenticated the secret");
 }
 private static string Decrypt(byte[] selected,Guid tenant,string purpose,string value)
 {
  var data=Convert.FromBase64String(value);if(data.Length<28)throw new CryptographicException("Invalid envelope");
  var plain=new byte[data.Length-28];
  try{using var aes=new AesGcm(selected,16);aes.Decrypt(data.AsSpan(0,12),data.AsSpan(28),data.AsSpan(12,16),plain,Encoding.UTF8.GetBytes($"v1:{tenant}:{purpose}"));return Encoding.UTF8.GetString(plain);}
  finally{CryptographicOperations.ZeroMemory(plain);}
 }
 public void Dispose(){foreach(var bytes in keys.Values)CryptographicOperations.ZeroMemory(bytes);}
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
