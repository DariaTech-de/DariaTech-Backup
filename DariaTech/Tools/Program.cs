using System.Security.Cryptography;
using System.Text.Json;
using DariaTech.Contracts;

if(args.Length!=5||args[0]!="sign-update")
{
 Console.Error.WriteLine("Usage: DariaTech.Tools sign-update PRIVATE_KEY MANIFEST_JSON ARTIFACT OUTPUT_JSON");return 2;
}
if(File.Exists(args[4]))throw new InvalidOperationException("Refusing to overwrite output manifest");
var manifest=JsonSerializer.Deserialize<AgentUpdateManifest>(File.ReadAllText(args[2]))??throw new InvalidOperationException("Manifest required");
if(!UpdateProtocol.Valid(manifest,DateTimeOffset.UtcNow))throw new InvalidOperationException("Invalid manifest or expiry");
using(var artifact=File.OpenRead(args[3]))
{
 if(artifact.Length!=manifest.Length||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(artifact),Convert.FromHexString(manifest.Sha256)))throw new CryptographicException("Manifest does not match artifact");
}
using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(args[1]));
if(key.KeySize!=256)throw new CryptographicException("Signing key must be ECDSA P-256");
File.WriteAllText(args[4],JsonSerializer.Serialize(UpdateProtocol.Sign(manifest,key)));
Console.WriteLine("Signed update manifest written. Keep the private key offline; publish only the signed manifest and verified artifact.");return 0;
