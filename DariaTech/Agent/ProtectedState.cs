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
  options=o;
  if(OperatingSystem.IsWindows())Directory.CreateDirectory(o.StateDirectory);else UnixPrivatePaths.Directory(o.StateDirectory,true);
 }
 public T? Read<T>(string name)
 {
  ValidateName(name);var path=Path.Combine(options.StateDirectory,name);if(!File.Exists(path))return default;
  if(!OperatingSystem.IsWindows())UnixPrivatePaths.File(path);
  var bytes=File.ReadAllBytes(path);byte[] plain;
  if(OperatingSystem.IsWindows())plain=ProtectedData.Unprotect(bytes,Encoding.UTF8.GetBytes(name),DataProtectionScope.CurrentUser);
  else
  {
   if(bytes.Length<28)throw new CryptographicException("Invalid state envelope");
   plain=new byte[bytes.Length-28];using var aes=new AesGcm(Key(),16);aes.Decrypt(bytes.AsSpan(0,12),bytes.AsSpan(28),bytes.AsSpan(12,16),plain,Encoding.UTF8.GetBytes(name));
  }
  try{return JsonSerializer.Deserialize<T>(plain);}finally{CryptographicOperations.ZeroMemory(plain);}
 }
 public void Write<T>(string name,T value)
 {
  ValidateName(name);if(!OperatingSystem.IsWindows())UnixPrivatePaths.Directory(options.StateDirectory,false);
  var plain=JsonSerializer.SerializeToUtf8Bytes(value);byte[] data;
  if(OperatingSystem.IsWindows())data=ProtectedData.Protect(plain,Encoding.UTF8.GetBytes(name),DataProtectionScope.CurrentUser);
  else
  {
   var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var encrypted=new byte[plain.Length];using var aes=new AesGcm(Key(),16);aes.Encrypt(nonce,plain,encrypted,tag,Encoding.UTF8.GetBytes(name));data=nonce.Concat(tag).Concat(encrypted).ToArray();
  }
  var target=Path.Combine(options.StateDirectory,name);var temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
  if(!OperatingSystem.IsWindows()&&File.Exists(target))UnixPrivatePaths.File(target);
  var creation=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
  if(!OperatingSystem.IsWindows())creation.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
  try{using(var f=new FileStream(temp,creation)){f.Write(data);f.Flush(true);}File.Move(temp,target,true);}
  finally{if(File.Exists(temp))File.Delete(temp);CryptographicOperations.ZeroMemory(plain);}
 }
 private static void ValidateName(string name){if(string.IsNullOrWhiteSpace(name)||name!=Path.GetFileName(name)||name is "." or "..")throw new InvalidOperationException("Invalid protected state filename");}
 private byte[] Key()
 {
  if(options.LinuxKeyFile is null)throw new InvalidOperationException("Agent:LinuxKeyFile required on non-Windows systems");
  UnixPrivatePaths.File(options.LinuxKeyFile);
  if(new FileInfo(options.LinuxKeyFile).Length>128)throw new InvalidOperationException("Invalid agent key file length");
  var key=Convert.FromBase64String(File.ReadAllText(options.LinuxKeyFile).Trim());if(key.Length!=32)throw new InvalidOperationException("Agent key must be 256 bits");return key;
 }
}
