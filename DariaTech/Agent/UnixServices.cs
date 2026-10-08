using System.Diagnostics;
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
   using var config=JsonDocument.Parse(File.ReadAllText(configuration));var state=config.RootElement.GetProperty("Agent").GetProperty("StateDirectory").GetString()!;
   UnixPrivatePaths.Directory(state,false);var log=Path.Combine(state,"agent-service.log");
   if(!File.Exists(log))UnixProvisioning.WritePrivate(log,"");
   var dictionary=new XElement("dict",
    new XElement("key","Label"),new XElement("string",MacLabel),
    new XElement("key","ProgramArguments"),new XElement("array",new XElement("string",executable),new XElement("string","--agent-config"),new XElement("string",configuration)),
    new XElement("key","RunAtLoad"),new XElement("true"),
    new XElement("key","KeepAlive"),new XElement("true"),
    new XElement("key","ProcessType"),new XElement("string","Background"),
    new XElement("key","Umask"),new XElement("integer","63"),
    new XElement("key","AbandonProcessGroup"),new XElement("false"),
    new XElement("key","ExitTimeOut"),new XElement("integer","45"),
    new XElement("key","ThrottleInterval"),new XElement("integer","10"),
    new XElement("key","StandardOutPath"),new XElement("string",log),
    new XElement("key","StandardErrorPath"),new XElement("string",log));
   content=new XDocument(new XDeclaration("1.0","UTF-8",null),new XDocumentType("plist","-//Apple//DTD PLIST 1.0//EN","http://www.apple.com/DTDs/PropertyList-1.0.dtd",null),new XElement("plist",new XAttribute("version","1.0"),dictionary)).ToString();
  }
  else
  {
   var command=Quote(executable)+" --agent-config "+Quote(configuration);
   content="[Unit]\nDescription=DariaTech Backup Agent\nAfter=network-online.target\nWants=network-online.target\n[Service]\nType=simple\nExecStart="+command+"\nUser=root\nUMask=0077\nRestart=on-failure\nRestartSec=10\nTimeoutStopSec=45\nKillMode=control-group\nNoNewPrivileges=true\nProtectKernelTunables=true\nProtectKernelModules=true\nProtectControlGroups=true\nRestrictSUIDSGID=true\n[Install]\nWantedBy=multi-user.target\n";
  }
  UnixPrivatePaths.TrustedFile(executable);WriteDefinition(path,content);
 }
 public static void WriteDefinition(string path,string content)
 {
  if(OperatingSystem.IsWindows()||UnixPrivatePaths.EffectiveUserId!=0)throw new InvalidOperationException("Service definition requires root");
  UnixPrivatePaths.TrustedDirectory(Path.GetDirectoryName(path)!);
  if(File.Exists(path))UnixPrivatePaths.TrustedFile(path);
  var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try
  {
   using(var stream=new FileStream(temporary,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.OtherRead}))
   using(var writer=new StreamWriter(stream)){writer.Write(content);writer.Flush();stream.Flush(true);}
   File.Move(temporary,path,true);
  }finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
 public static async Task Stop(CancellationToken ct)
 {
  RequireRoot();
  if(OperatingSystem.IsMacOS())
  {
   if(!await Exists(ct))return;
   await Control("/bin/launchctl",["bootout","system/"+MacLabel],ct);
   // bootout can acknowledge removal before launchd finishes draining the job.
   for(var i=0;i<120;i++){if(!await Exists(ct))return;await Task.Delay(500,ct);}
   throw new InvalidOperationException("launchd service did not finish unloading");
  }
  if(await Control("/usr/bin/systemctl",["stop",ServiceName+".service"],ct)!=0)throw new InvalidOperationException("systemd rejected service stop");
 }
 public static async Task Start(CancellationToken ct)
 {
  RequireRoot();
  if(OperatingSystem.IsMacOS())
  {
   var plist="/Library/LaunchDaemons/"+MacLabel+".plist";UnixPrivatePaths.TrustedFile(plist);
   for(var i=0;i<40;i++)
   {
    if(await Control("/bin/launchctl",["bootstrap","system",plist],ct)==0)return;
    // A successful asynchronous registration is visible even when launchctl's
    // immediate result is transient EIO. Stop() must have drained the old job.
    if(await Exists(ct))return;
    await Task.Delay(500,ct);
   }
   throw new InvalidOperationException("launchd rejected service start");
  }
  if(await Control("/usr/bin/systemctl",["start",ServiceName+".service"],ct)!=0)throw new InvalidOperationException("systemd rejected service start");
 }
 private static void RequireRoot()
 {
  if(OperatingSystem.IsWindows()||UnixPrivatePaths.EffectiveUserId!=0)throw new InvalidOperationException("Native service control requires root");
 }
 private static async Task<bool> Exists(CancellationToken ct)=>await Control("/bin/launchctl",["print","system/"+MacLabel],ct)==0;
 private static async Task<int> Control(string executable,string[] args,CancellationToken ct)
 {
  var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);
  using var process=Process.Start(info)??throw new InvalidOperationException("Native service control unavailable");
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(60));
  var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
  try{await process.WaitForExitAsync(timeout.Token);await stdout;await stderr;return process.ExitCode;}
  finally{if(!process.HasExited)process.Kill(true);}
 }
 private static string Quote(string value)
 {
  if(value.Any(c=>char.IsControl(c)||c is '"' or '%' or '$'))throw new InvalidOperationException("Unsafe service path");
  return "\""+value.Replace("\\","\\\\")+"\"";
 }
}
