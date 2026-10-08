using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace DariaTech.Agent;

public static class UnixServices
{
 public static string ServiceName
 {
  get
  {
   using var product=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"product.json")));
   var name=product.RootElement.GetProperty("windowsServiceName").GetString()!;
   if(!Regex.IsMatch(name,@"^[A-Za-z][A-Za-z0-9]{1,60}$"))throw new InvalidOperationException("Invalid service identity");
   return name;
  }
 }
 public static string MacLabel=>"de.dariatech."+ServiceName.ToLowerInvariant();
 public static void Write(string configuration)
 {
  if(OperatingSystem.IsWindows()||UnixPrivatePaths.EffectiveUserId!=0)throw new InvalidOperationException("Unix service definition requires root");
  UnixPrivatePaths.File(configuration);
  var executable=Path.Combine(AppContext.BaseDirectory,"DariaTech.Agent");
  var path=OperatingSystem.IsMacOS()?"/Library/LaunchDaemons/"+MacLabel+".plist":"/etc/systemd/system/"+ServiceName+".service";
  string content;
  if(OperatingSystem.IsMacOS())
  {
   var dictionary=new XElement("dict",
    new XElement("key","Label"),new XElement("string",MacLabel),
    new XElement("key","ProgramArguments"),new XElement("array",new XElement("string",executable),new XElement("string","--agent-config"),new XElement("string",configuration)),
    new XElement("key","RunAtLoad"),new XElement("true"),
    new XElement("key","KeepAlive"),new XElement("true"),
    new XElement("key","ProcessType"),new XElement("string","Background"),
    new XElement("key","Umask"),new XElement("integer","63"),
    new XElement("key","AbandonProcessGroup"),new XElement("false"),
    new XElement("key","ExitTimeOut"),new XElement("integer","45"),
    new XElement("key","ThrottleInterval"),new XElement("integer","10"));
   content=new XDocument(new XDeclaration("1.0","UTF-8",null),new XDocumentType("plist","-//Apple//DTD PLIST 1.0//EN","http://www.apple.com/DTDs/PropertyList-1.0.dtd",null),new XElement("plist",new XAttribute("version","1.0"),dictionary)).ToString();
  }
  else
  {
   var command=Quote(executable)+" --agent-config "+Quote(configuration);
   content="[Unit]\nDescription=DariaTech Backup Agent\nAfter=network-online.target\nWants=network-online.target\n[Service]\nType=simple\nExecStart="+command+"\nUser=root\nUMask=0077\nRestart=on-failure\nRestartSec=10\nTimeoutStopSec=45\nKillMode=control-group\nNoNewPrivileges=true\nProtectKernelTunables=true\nProtectKernelModules=true\nProtectControlGroups=true\nRestrictSUIDSGID=true\n[Install]\nWantedBy=multi-user.target\n";
  }
  UnixPrivatePaths.TrustedFile(executable);
  if(File.Exists(path))UnixPrivatePaths.TrustedFile(path);
  var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try
  {
   using(var stream=new FileStream(temporary,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.OtherRead}))
   using(var writer=new StreamWriter(stream)){writer.Write(content);writer.Flush();stream.Flush(true);}
   File.Move(temporary,path,true);
  }finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
 private static string Quote(string value)
 {
  if(value.Any(c=>char.IsControl(c)||c is '"' or '%' or '$'))throw new InvalidOperationException("Unsafe service path");
  return "\""+value.Replace("\\","\\\\")+"\"";
 }
}
