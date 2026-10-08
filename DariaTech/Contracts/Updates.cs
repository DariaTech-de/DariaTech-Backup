using System.Security.Cryptography;
using System.Text.Json;
namespace DariaTech.Contracts;
public sealed record AgentUpdateManifest(Guid ReleaseId,long Sequence,string Product,string Platform,string Version,string ArtifactUrl,string Sha256,long Length,DateTimeOffset Issued,DateTimeOffset Expires);
public sealed record SignedUpdate(string Payload,string Signature);
public sealed record UpdateAssignment(Guid DeploymentId,SignedUpdate Manifest);
public sealed record UpdateReceipt(string Status,string? ErrorCode);
public static class UpdateProtocol
{
 public static readonly string[] Platforms=["win-x64","linux-x64","linux-arm64","osx-x64","osx-arm64"];
 public static bool Valid(AgentUpdateManifest m,DateTimeOffset now)=>m.ReleaseId!=Guid.Empty&&m.Sequence>0&&m.Product=="DariaTechBackupAgent"&&Platforms.Contains(m.Platform,StringComparer.Ordinal)
  &&System.Version.TryParse(m.Version,out var v)&&v.Revision>=0&&v.Build>=0&&m.Version==v.ToString()
  &&m.Length is >=1024 and <=1073741824&&m.Sha256?.Length==64&&m.Sha256.All(Uri.IsHexDigit)
  &&Uri.TryCreate(m.ArtifactUrl,UriKind.Absolute,out var url)&&url.Scheme=="https"&&url.IsDefaultPort&&url.UserInfo==""&&url.Fragment==""&&url.Query==""
  &&m.Issued<=now.AddSeconds(30)&&m.Expires>now&&m.Expires>m.Issued&&m.Expires-m.Issued<=TimeSpan.FromDays(90);
 public static SignedUpdate Sign(AgentUpdateManifest manifest,ECDsa key)
 {var data=JsonSerializer.SerializeToUtf8Bytes(manifest);return new(Convert.ToBase64String(data),Convert.ToBase64String(key.SignData(data,HashAlgorithmName.SHA256)));}
 public static AgentUpdateManifest Verify(SignedUpdate signed,ECDsa key,DateTimeOffset now)
 {
  if(signed.Payload.Length>16000||signed.Signature.Length>256)throw new CryptographicException("Invalid update envelope");
  var data=Convert.FromBase64String(signed.Payload);if(!key.VerifyData(data,Convert.FromBase64String(signed.Signature),HashAlgorithmName.SHA256))throw new CryptographicException("Invalid update signature");
  var manifest=JsonSerializer.Deserialize<AgentUpdateManifest>(data)??throw new CryptographicException("Missing update manifest");if(!Valid(manifest,now))throw new CryptographicException("Invalid or expired update manifest");return manifest;
 }
 public static bool Newer(AgentUpdateManifest manifest,string installed,long acceptedSequence,string? platform=null)=>(platform is null||manifest.Platform==platform)&&manifest.Sequence>acceptedSequence&&System.Version.TryParse(installed,out var current)&&System.Version.Parse(manifest.Version)>current;
}
