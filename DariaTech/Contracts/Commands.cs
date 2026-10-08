using System.Security.Cryptography;
using System.Text.Json;
namespace DariaTech.Contracts;

public enum RemoteAction { RunBackup, StopBackup, VerifyBackup, Restore, ListRestorePoints, ListRestoreFiles, RestoreSaas }
public sealed record RestoreSelection(DateTimeOffset Snapshot,string[] Paths,string DestinationFolder);
public sealed record CommandInput(Guid JobId,RemoteAction Action,RestoreSelection? Restore,int ValidMinutes,CatalogRequest? Catalog=null,
 [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SaasRestoreSelection? SaasRestore=null);
public sealed record DeviceCommand(Guid Id,Guid TenantId,Guid DeviceId,string LocalJobId,RemoteAction Action,RestoreSelection? Restore,DateTimeOffset Issued,DateTimeOffset Expires,CatalogRequest? Catalog=null,
 [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SaasRestoreSelection? SaasRestore=null);
public sealed record SignedCommand(string Payload,string Signature);
public sealed record CommandReceipt(string Status,long? TaskId,string? ErrorCode,RestoreCatalog? Catalog=null);
public static class CommandProtocol
{
 public static bool Valid(DeviceCommand c,Guid device,DateTimeOffset now)
 {
  if(c.Id==Guid.Empty||c.TenantId==Guid.Empty||c.DeviceId!=device||!long.TryParse(c.LocalJobId,out var id)||id<1||!Enum.IsDefined(c.Action)
   ||c.Expires<=now||c.Issued>now.AddSeconds(30)||c.Expires<=c.Issued||c.Expires-c.Issued>TimeSpan.FromMinutes(30))return false;
  if(c.Action==RemoteAction.RestoreSaas)return c.Restore is null&&c.Catalog is null&&SaasPolicy.ValidRestore(c.SaasRestore,now);
  if(c.SaasRestore is not null)return false;
  if(c.Action==RemoteAction.ListRestorePoints)return c.Restore is null&&c.Catalog is null;
  if(c.Action==RemoteAction.ListRestoreFiles)return c.Restore is null&&c.Catalog?.Snapshot is {} snapshot&&snapshot.Year>=2000&&snapshot<=now.AddMinutes(5)&&(c.Catalog.Prefix is null||c.Catalog.Prefix.Length<=1000&&!c.Catalog.Prefix.Any(char.IsControl));
  if(c.Catalog is not null)return false;
  if(c.Action!=RemoteAction.Restore)return c.Restore is null;
  var s=c.Restore;return s is not null&&s.Paths is not null&&s.Paths.Length is >0 and <=500&&s.Paths.All(p=>!string.IsNullOrWhiteSpace(p)&&p.Length<=1000&&!p.Any(char.IsControl))
   &&s.Snapshot.Year>=2000&&s.Snapshot<=now.AddMinutes(5)&&SafeFolder(s.DestinationFolder);
 }
 public static bool SafeFolder(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=100&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
 public static SignedCommand Sign(DeviceCommand command,ECDsa key)
 {
  var bytes=JsonSerializer.SerializeToUtf8Bytes(command);return new(Convert.ToBase64String(bytes),Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256)));
 }
 public static DeviceCommand Verify(SignedCommand envelope,ECDsa key,Guid device,DateTimeOffset now)
 {
  if(envelope.Payload.Length>128000||envelope.Signature.Length>256)throw new CryptographicException("Invalid command envelope");
  var bytes=Convert.FromBase64String(envelope.Payload);var signature=Convert.FromBase64String(envelope.Signature);
  if(!key.VerifyData(bytes,signature,HashAlgorithmName.SHA256))throw new CryptographicException("Invalid command signature");
  var command=JsonSerializer.Deserialize<DeviceCommand>(bytes)??throw new CryptographicException("Invalid command");
  if(!Valid(command,device,now))throw new CryptographicException("Expired or invalid command");return command;
 }
}

public sealed record CatalogRequest(DateTimeOffset? Snapshot,string? Prefix);
public sealed record RestorePoint(DateTimeOffset Time,long? Files,long? Bytes);
public sealed record RestoreFile(string Path,long? Bytes,bool Directory);
public sealed record RestoreCatalog(RestorePoint[] Points,RestoreFile[] Files,bool Truncated);
