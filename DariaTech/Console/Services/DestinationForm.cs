using DariaTech.Contracts;
namespace DariaTech.Console.Services;

// Presentation and URL mapping for the destination picker. The option list itself comes from the engine
// (DestinationCatalog); this file only adds German labels, grouping and the URL shape per destination.
public static class DestinationForm
{
 // Shown first in the picker; everything else is listed under "Weitere Ziele".
 public static readonly string[] Recommended=["file","smb","ssh","webdav","s3","b2","e2","azure","gcs","ftp","onedrivev2","googledrive","dropbox","sharepoint"];
 static readonly string[] OAuthFolder=["googledrive","onedrivev2","dropbox","box","pcloud","jottacloud","msgroup","sharepoint"];

 public static string Label(DestinationType t)=>t.Key switch
 {
  "file"=>"Lokaler Ordner oder Laufwerk","smb"=>"Netzwerkfreigabe (SMB/CIFS, NAS)","ssh"=>"SFTP (SSH)","webdav"=>"WebDAV (z. B. Nextcloud)",
  "s3"=>"S3-kompatibel (AWS, Wasabi, MinIO …)","b2"=>"Backblaze B2","e2"=>"IDrive e2","azure"=>"Azure Blob Storage","gcs"=>"Google Cloud Storage",
  "ftp"=>"FTP (mit TLS)","aftp"=>"FTP, alternative Implementierung (mit TLS)","onedrivev2"=>"Microsoft OneDrive","googledrive"=>"Google Drive",
  "sharepoint"=>"Microsoft SharePoint","msgroup"=>"Microsoft-365-Gruppe",_=>t.Name.Trim(),
 };
 public static string HostLabel(string key)=>key switch
 {
  "s3" or "b2" or "e2" or "gcs"=>"Bucket","azure"=>"Container","openstack"=>"Container",
  _ when OAuthFolder.Contains(key)=>"Ordner",
  _=>"Server",
 };
 public static bool HasHost(string key)=>key is not ("file" or "storj" or "cos" or "aliyunoss");
 public static bool HasPort(string key)=>key is "ssh" or "ftp" or "aftp" or "webdav" or "smb" or "tahoe";
 public static bool HasPath(string key)=>!OAuthFolder.Contains(key);
 public static string PathLabel(string key)=>key=="file"?"Ordner auf dem Gerät (z. B. D:\\Backup oder /mnt/backup)":"Ordner / Präfix";
 public static string? Hint(string key)=>key switch
 {
  _ when OAuthFolder.Contains(key)||key=="gcs"=>"Die AuthID erzeugen Sie unter oauth-service.duplicati.com mit dem Konto des Speicherziels.",
  "ssh"=>"Der Host-Key-Fingerprint ist Pflicht, damit sich der Agent nur mit dem echten Server verbindet.",
  "ftp" or "aftp"=>"Unverschlüsseltes FTP wird nicht unterstützt; wählen Sie Explicit oder Implicit TLS.",
  "s3" or "webdav"=>"Die Verbindung erfolgt immer über TLS (HTTPS).",
  "file"=>"Der Ordner muss auf dem Gerät existieren oder anlegbar sein, z. B. eine externe Festplatte.",
  _=>null,
 };

 static readonly Dictionary<string,string> Labels=new(StringComparer.OrdinalIgnoreCase)
 {
  ["auth-username"]="Benutzername",["auth-password"]="Passwort",["auth-domain"]="Domäne",["authid"]="AuthID",
  ["aws-access-key-id"]="Access Key ID",["aws-secret-access-key"]="Secret Access Key",["s3-server-name"]="S3-Server (Endpoint)",
  ["s3-location-constraint"]="Region / Location",["s3-storage-class"]="Speicherklasse",["use-ssl"]="TLS (HTTPS) verwenden",
  ["b2-accountid"]="Key ID / Account ID",["b2-applicationkey"]="Application Key",["azure-account-name"]="Speicherkonto",
  ["azure-access-key"]="Zugriffsschlüssel",["azure-access-sas-token"]="SAS-Token",["ssh-fingerprint"]="Host-Key-Fingerprint",
  ["ssh-key"]="Privater SSH-Schlüssel (Inhalt)",["ftp-encryption-mode"]="Verschlüsselung",["aftp-encryption-mode"]="Verschlüsselung",
  ["gcs-project"]="Projekt-ID",["gcs-location"]="Standort",["gcs-storage-class"]="Speicherklasse",["gcs-service-account-json"]="Service-Account-JSON",
  ["access_key_id"]="Access Key ID",["access_key_secret"]="Secret Access Key",["transport"]="Transport",
  ["googledrive-teamdrive-id"]="Geteilte Ablage (ID)",["drive-id"]="Laufwerk-ID",["site-id"]="Website-ID",["group-email"]="Gruppen-E-Mail",
  ["openstack-authuri"]="Auth-URI",["openstack-tenant-name"]="Tenant",["openstack-region"]="Region",["openstack-apikey"]="API-Key",["openstack-domain-name"]="Domäne",
  ["storj-satellite"]="Satellit",["storj-api-key"]="API-Key",["storj-secret"]="Verschlüsselungs-Passphrase",["storj-bucket"]="Bucket",["storj-folder"]="Ordner",
 };
 public static string OptionLabel(DestinationOption o)=>Labels.TryGetValue(o.Name,out var l)?l:o.Name;
 public static bool Advanced(DestinationOption o)=>!o.Secret&&!Labels.ContainsKey(o.Name)&&!o.Name.Contains("bucket",StringComparison.OrdinalIgnoreCase)&&
  !o.Name.Contains("region",StringComparison.OrdinalIgnoreCase)&&!o.Name.EndsWith("-id",StringComparison.OrdinalIgnoreCase)&&!o.Name.Contains("endpoint",StringComparison.OrdinalIgnoreCase);
 public static IEnumerable<DestinationOption> Ordered(DestinationType t)=>t.Options.OrderBy(o=>Advanced(o)).ThenBy(o=>Labels.ContainsKey(o.Name)?Array.IndexOf(Labels.Keys.ToArray(),o.Name):int.MaxValue).ThenBy(o=>o.Name);
 // Options the transport rules require are fixed on, so they are not offered as a choice.
 public static IReadOnlyDictionary<string,string> Forced(string key)=>key switch{"s3" or "webdav" or "tahoe"=>new Dictionary<string,string>{["use-ssl"]="true"},_=>new Dictionary<string,string>()};

 public sealed record Parts(string Key,string Host,string Port,string Path);
 public sealed record Picker(string Current,Parts Dest,IReadOnlyDictionary<string,string> Values,IReadOnlySet<string> Stored,bool Locked);
 public static Parts Parse(string? url)
 {
  if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||DestinationCatalog.Resolve(uri.Scheme) is not {Type:{} t})return new("","","","");
  if(t.Key=="file")
  {
   var p=Uri.UnescapeDataString(uri.AbsolutePath);
   if(p.Length>2&&p[0]=='/'&&p[2]==':')p=p[1..].Replace('/','\\');
   return new(t.Key,"","",p);
  }
  return new(t.Key,Uri.UnescapeDataString(uri.Host),uri.IsDefaultPort||uri.Port<0?"":uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')));
 }
 public static string? Build(string key,string? host,string? port,string? path)
 {
  host=(host??"").Trim();port=(port??"").Trim();path=(path??"").Trim();
  if(DestinationCatalog.Find(key) is null||host.Any(c=>c is '/' or '\\' or '@' or '?' or '#' or ':'||char.IsWhiteSpace(c)))return null;
  if(key=="file")
  {
   if(path.Length==0||path.StartsWith(@"\\",StringComparison.Ordinal))return null; // network shares use the SMB destination
   var normalized=path.Replace('\\','/');
   if(!normalized.StartsWith('/'))normalized="/"+normalized;
   return "file://"+string.Join('/',normalized.Split('/').Select(Uri.EscapeDataString));
  }
  if(HasHost(key)&&host.Length==0)return null;
  if(port.Length>0&&(!int.TryParse(port,out var p)||p is <1 or >65535))return null;
  var segments=path.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
  var authority=HasHost(key)?Uri.EscapeDataString(host)+(port.Length>0?":"+port:""):"";
  var url=key+"://"+authority+(HasPath(key)?"/"+string.Join('/',segments):"");
  return Uri.TryCreate(url,UriKind.Absolute,out _)?url:null;
 }
 // Reads the picker fields of one destination from a posted form. Blank secrets keep the previous value.
 public static (string? Url,Dictionary<string,string> Options) Read(Microsoft.AspNetCore.Http.IFormCollection form,string key,IReadOnlyDictionary<string,string>? previous=null)
 {
  var options=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  if(DestinationCatalog.Find(key) is not {} type)return (null,options);
  string Field(string name)=>form[$"dest.{key}.{name}"].ToString().Trim();
  foreach(var option in type.Options)
  {
   var value=option.Secret?form[$"dest.{key}.opt.{option.Name}"].ToString():Field("opt."+option.Name);
   if(option.Secret&&value.Length==0&&previous?.FirstOrDefault(x=>string.Equals(x.Key,option.Name,StringComparison.OrdinalIgnoreCase)).Value is {} kept)value=kept;
   if(value.Length>0)options[option.Name]=value;
  }
  foreach(var (k,v) in Forced(type.Key))options[k]=v;
  return (Build(type.Key,Field("host"),Field("port"),Field("path")),options);
 }
 // Each job needs its own folder below a shared destination; Duplicati refuses two backups in one folder.
 public static string? Below(string baseUrl,params string[] segments)
 {
  var parts=Parse(baseUrl);if(parts.Key.Length==0)return null;
  var clean=segments.Select(Slug).Where(x=>x.Length>0).ToArray();
  if(parts.Key=="file")
  {
   var sep=parts.Path.Contains('\\')||parts.Path.Length>1&&parts.Path[1]==':'?"\\":"/";
   return Build("file","","",parts.Path.TrimEnd('\\','/')+sep+string.Join(sep,clean));
  }
  var path=string.Join('/',new[]{parts.Path.Trim('/')}.Where(x=>x.Length>0).Concat(clean));
  if(HasPath(parts.Key))return Build(parts.Key,parts.Host,parts.Port,path);
  var url=parts.Key+"://"+Uri.EscapeDataString(parts.Host)+"/"+string.Join('/',clean.Select(Uri.EscapeDataString)); // folder-style destinations (OAuth drives)
  return Uri.TryCreate(url,UriKind.Absolute,out _)?url:null;
 }
 public static string Slug(string value)
 {
  var chars=value.Trim().Select(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.'?c:'-').ToArray();
  return new string(chars).Trim('-','.').Replace("--","-");
 }
 // Short, secret-free description for job lists.
 public static string Describe(string? url)
 {
  var p=Parse(url);
  if(DestinationCatalog.Find(p.Key) is not {} t)return "Unbekanntes Ziel";
  var where=p.Key=="file"?p.Path:string.Join('/',new[]{p.Host,p.Path}.Where(x=>x.Length>0));
  return where.Length==0?Label(t):Label(t)+" · "+where;
 }
}
