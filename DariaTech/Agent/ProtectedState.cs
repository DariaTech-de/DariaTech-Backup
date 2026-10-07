using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DariaTech.Agent;

// Windows: DPAPI under the service identity. Unix: explicit external 256-bit key.
public sealed class ProtectedState
{
 private readonly AgentOptions options;
 public ProtectedState(AgentOptions o)
 {
  options=o;Directory.CreateDirectory(o.StateDirectory);
  if(!OperatingSystem.IsWindows())File.SetUnixFileMode(o.StateDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
 }
 public T? Read<T>(string name)
 {
  var path=Path.Combine(options.StateDirectory,name);if(!File.Exists(path))return default;
  var bytes=File.ReadAllBytes(path);byte[] plain;
  if(OperatingSystem.IsWindows())plain=ProtectedData.Unprotect(bytes,Encoding.UTF8.GetBytes(name),DataProtectionScope.CurrentUser);
  else
  {
   if(bytes.Length<28)throw new CryptographicException("Invalid state envelope");
   plain=new byte[bytes.Length-28];using var aes=new AesGcm(Key(),16);aes.Decrypt(bytes.AsSpan(0,12),bytes.AsSpan(28),bytes.AsSpan(12,16),plain,Encoding.UTF8.GetBytes(name));
  }
  return JsonSerializer.Deserialize<T>(plain);
 }
 public void Write<T>(string name,T value)
 {
  var plain=JsonSerializer.SerializeToUtf8Bytes(value);byte[] data;
  if(OperatingSystem.IsWindows())data=ProtectedData.Protect(plain,Encoding.UTF8.GetBytes(name),DataProtectionScope.CurrentUser);
  else
  {
   var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var encrypted=new byte[plain.Length];using var aes=new AesGcm(Key(),16);aes.Encrypt(nonce,plain,encrypted,tag,Encoding.UTF8.GetBytes(name));data=nonce.Concat(tag).Concat(encrypted).ToArray();
  }
  var target=Path.Combine(options.StateDirectory,name);var temp=target+".tmp";
  using(var f=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite);f.Write(data);f.Flush(true);}
  File.Move(temp,target,true);
 }
 private byte[] Key()
 {
  if(options.LinuxKeyFile is null)throw new InvalidOperationException("Agent:LinuxKeyFile required on non-Windows systems");
  var key=Convert.FromBase64String(File.ReadAllText(options.LinuxKeyFile).Trim());if(key.Length!=32)throw new InvalidOperationException("Agent key must be 256 bits");return key;
 }
}
