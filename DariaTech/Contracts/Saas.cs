using System.Net.Mail;
using System.Text.Json;
namespace DariaTech.Contracts;

public enum SaasProvider { Microsoft365, GoogleWorkspace }
// Source credentials remain inside the encrypted configuration revision. Never emit this
// type from a management GET or telemetry endpoint.
public sealed record SaasSource(SaasProvider Provider,string DirectoryTenant,
 Dictionary<string,string> Credentials,string[] RootTypes,string[] UserTypes);
public sealed record SaasRestoreSelection(long ConfigurationRevision,DateTimeOffset Snapshot,
 string[] Paths,string TargetPath,bool ConfirmProviderWrites);

public static class SaasPolicy
{
 public static string Key(SaasProvider provider)=>provider switch
 {SaasProvider.Microsoft365=>"office365",SaasProvider.GoogleWorkspace=>"googleworkspace",_=>throw new InvalidOperationException("Unknown SaaS provider")};
 public static bool Valid(SaasSource? source)
 {
  if(source is null||string.IsNullOrWhiteSpace(source.DirectoryTenant)||!Enum.IsDefined(source.Provider)||source.Credentials is null||source.Credentials.Count>6||
   source.RootTypes is null||source.UserTypes is null||source.RootTypes.Length is <1 or >8||source.UserTypes.Length is <1 or >12||
   source.RootTypes.Distinct().Count()!=source.RootTypes.Length||source.UserTypes.Distinct().Count()!=source.UserTypes.Length)return false;
  if(source.Provider==SaasProvider.Microsoft365)
  {
   if(!Guid.TryParseExact(source.DirectoryTenant,"D",out var tenant)||tenant==Guid.Empty)return false;
   if(!Subset(source.RootTypes,["Users","Groups","Sites"])||!Subset(source.UserTypes,["Profile","Mailbox","Calendar","Contacts","Tasks","Notes","Planner","Chats"]))return false;
   if(source.Credentials.Keys.Any(x=>x is not ("office365-client-id" or "office365-client-secret")))return false;
   return source.Credentials.TryGetValue("office365-client-id",out var client)&&Guid.TryParseExact(client,"D",out var clientId)&&clientId!=Guid.Empty&&
    source.Credentials.TryGetValue("office365-client-secret",out var secret)&&Secret(secret,4000);
  }
  if(Uri.CheckHostName(source.DirectoryTenant)!=UriHostNameType.Dns||!source.DirectoryTenant.Contains('.')||
     source.DirectoryTenant!=source.DirectoryTenant.ToLowerInvariant())return false;
  if(!Subset(source.RootTypes,["Users","Groups","SharedDrives","Sites","OrganizationalUnits"])||
     !Subset(source.UserTypes,["Gmail","Drive","Calendar","Contacts","Tasks","Keep","Chat"]))return false;
  if(source.Credentials.Keys.Any(x=>x is not ("google-client-id" or "google-client-secret" or "google-refresh-token" or "google-service-account-json" or "google-admin-email")))return false;
  if(!source.Credentials.TryGetValue("google-admin-email",out var admin)||!MailAddress.TryCreate(admin,out var email)||email.Address!=admin||
     !email.Host.Equals(source.DirectoryTenant,StringComparison.OrdinalIgnoreCase))return false;
  if(source.Credentials.TryGetValue("google-service-account-json",out var json))
  {
   if(source.Credentials.Count!=2||string.IsNullOrWhiteSpace(json)||json.Length>16000)return false;
   try
   {
    using var document=JsonDocument.Parse(json);var root=document.RootElement;
    if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Select(x=>x.Name).Distinct().Count()!=root.EnumerateObject().Count()||root.TryGetProperty("universe_domain",out var universe)&&universe.GetString()!="googleapis.com")return false;
    return root.GetProperty("type").GetString()=="service_account"&&
     root.GetProperty("token_uri").GetString()=="https://oauth2.googleapis.com/token"&&
     root.GetProperty("client_email").GetString() is {} address&&MailAddress.TryCreate(address,out var account)&&account.Host.EndsWith(".iam.gserviceaccount.com",StringComparison.Ordinal)&&
     root.GetProperty("private_key").GetString() is {} key&&key.StartsWith("-----BEGIN PRIVATE KEY-----",StringComparison.Ordinal)&&key.Length<=10000;
   }catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException){return false;}
  }
  return source.Credentials.Count==4&&source.Credentials.TryGetValue("google-client-id",out var id)&&Secret(id,1000)&&
   source.Credentials.TryGetValue("google-client-secret",out var password)&&Secret(password,4000)&&
   source.Credentials.TryGetValue("google-refresh-token",out var refresh)&&Secret(refresh,4000);
 }
 public static bool SameDirectory(SaasSource? previous,SaasSource? next)=>previous is null?next is null:
  next is not null&&previous.Provider==next.Provider&&previous.DirectoryTenant==next.DirectoryTenant;
 public static Dictionary<string,string> Options(SaasSource source)
 {
  if(!Valid(source))throw new InvalidOperationException("SaaS source rejected");
  var result=new Dictionary<string,string>(source.Credentials,StringComparer.Ordinal)
  {["store-metadata-content-in-database"]="true",["abort-if-source-missing"]="true"};
  var prefix=source.Provider==SaasProvider.Microsoft365?"office365":"google";
  result[prefix+"-included-root-types"]=string.Join(',',source.RootTypes);
  result[prefix+"-included-user-types"]=string.Join(',',source.UserTypes);
  if(source.Provider==SaasProvider.Microsoft365)result["office365-tenant-id"]=source.DirectoryTenant;
  return result;
 }
 public static bool SafeTarget(string? path)=>!string.IsNullOrWhiteSpace(path)&&path.Length<=1000&&
  path.Split('/').All(p=>!string.IsNullOrWhiteSpace(p)&&p.Length<=240&&p is not ("." or "..")&&!p.Any(c=>char.IsControl(c)||"\\?&#|%:*$".Contains(c)));
 public static bool ValidRestore(SaasRestoreSelection? restore,DateTimeOffset now)=>restore is not null&&restore.ConfigurationRevision>0&&restore.ConfirmProviderWrites&&
  restore.Snapshot.Year>=2000&&restore.Snapshot<=now.AddMinutes(5)&&SafeTarget(restore.TargetPath)&&
  restore.Paths is {Length:>0 and <=500}&&restore.Paths.All(p=>!string.IsNullOrWhiteSpace(p)&&p.Length<=1000&&!p.Any(char.IsControl));
 private static bool Subset(string[] input,string[] allowed)=>input.All(x=>allowed.Contains(x,StringComparer.Ordinal));
 private static bool Secret(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&!value.Any(char.IsControl);
}
