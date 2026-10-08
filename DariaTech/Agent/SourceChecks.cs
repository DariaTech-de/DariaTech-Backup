using System.Diagnostics;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed record ManagedSourceBinding(Guid JobId,long Revision,string[] Sources,SourceMountRequirement[] Mounts,ProxmoxSource? Proxmox);
public sealed record MountedSource(string MountPoint,string FileSystemType,string RemoteSource);

public static class SourceChecks
{
 public static bool Contains(string root,string path)
 {
  var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
  var fullRoot=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  var fullPath=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  return fullPath.StartsWith(fullRoot,comparison);
 }
 private static bool Overlaps(string first,string second)=>Contains(first,second)||Contains(second,first);
 public static SourceMountRequirement[] RequiredMounts(AgentOptions options,ManagedBackupDefinition definition)=>
  (definition.SourceMounts??[]).Concat(options.RequiredSourceMounts.Where(m=>definition.Sources.Any(p=>Overlaps(m.MountPoint,p)))).Distinct().ToArray();
 public static void AuthorizeFiles(AgentOptions options,IEnumerable<string> sources)
 {
  if(options.AllowProxmoxSnapshots&&sources.Any(p=>!options.AllowedFileSourceRoots.Any(root=>Contains(root,p))))throw new InvalidOperationException("File sources are outside the host's local tenant boundary");
 }
 public static void RejectLinks(string path)
 {
  var directory=new DirectoryInfo(Path.GetFullPath(path));
  for(var current=directory;current is not null;current=current.Parent)
   if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Managed source ancestry contains a link");
 }
 public static async Task<MountedSource[]> ReadMounts(CancellationToken ct)
 {
  if(OperatingSystem.IsLinux())return ParseLinuxMounts(await File.ReadAllLinesAsync("/proc/self/mountinfo",ct));
  if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("Mount identity checks require Unix; Windows shares use explicit UNC paths and native missing-source rejection");
  var info=new ProcessStartInfo("/sbin/mount"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  using var process=Process.Start(info)??throw new InvalidOperationException("Mount table unavailable");
  var output=process.StandardOutput.ReadToEndAsync(ct);var errors=process.StandardError.ReadToEndAsync(ct);
  await process.WaitForExitAsync(ct);await errors;
  if(process.ExitCode!=0)throw new InvalidOperationException("Mount table unavailable");
  return ParseMacMounts((await output).Split('\n'));
 }
 public static MountedSource[] ParseLinuxMounts(IEnumerable<string> lines)
 {
  var records=new List<MountedSource>();
  foreach(var line in lines)
  {
   var sections=line.Split(" - ",StringSplitOptions.None);if(sections.Length!=2)continue;
   var left=sections[0].Split(' ',StringSplitOptions.RemoveEmptyEntries);var right=sections[1].Split(' ',StringSplitOptions.RemoveEmptyEntries);
   if(left.Length<6||right.Length<3)continue;
   // A bind mount of a subdirectory is not the declared remote-share root.
   if(Unescape(left[3])!="/")continue;
   records.Add(new(Unescape(left[4]),right[0],Unescape(right[1])));
  }
  return records.ToArray();
 }
 public static MountedSource[] ParseMacMounts(IEnumerable<string> lines)
 {
  var records=new List<MountedSource>();
  foreach(var line in lines)
  {
   var on=line.IndexOf(" on ",StringComparison.Ordinal);var options=line.LastIndexOf(" (",StringComparison.Ordinal);
   if(on<1||options<=on+4||!line.EndsWith(')'))continue;
   var fs=line[(options+2)..].Split(',')[0].TrimEnd(')');
   records.Add(new(line[(on+4)..options],fs,line[..on]));
  }
  return records.ToArray();
 }
 private static string Unescape(string text)=>text.Replace(@"\040"," ").Replace(@"\011","\t").Replace(@"\012","\n").Replace(@"\134",@"\");
 public static void ValidateMounts(IEnumerable<string> sources,IEnumerable<SourceMountRequirement> requirements,IEnumerable<MountedSource> mounted)
 {
  var paths=sources.ToArray();var actual=mounted.ToArray();
  foreach(var requirement in requirements)
  {
   if(!SourcePolicy.ValidMount(requirement)||!paths.Any(p=>Overlaps(requirement.MountPoint,p)))throw new InvalidOperationException("Mount requirement does not match this source");
   RejectLinks(requirement.MountPoint);
   var atMount=actual.Where(x=>Path.GetFullPath(x.MountPoint).TrimEnd('/')==Path.GetFullPath(requirement.MountPoint).TrimEnd('/')).ToArray();
   if(atMount.Length!=1||atMount[0].FileSystemType!=requirement.FileSystemType||atMount[0].RemoteSource!=requirement.RemoteSource)throw new InvalidOperationException("Required NAS share is not mounted with the expected identity");
   // Reject a nested foreign mount that would silently replace a portion of this backup.
   foreach(var nested in actual.Where(x=>x.MountPoint!=atMount[0].MountPoint&&Contains(requirement.MountPoint,x.MountPoint)&&paths.Any(p=>Overlaps(p,x.MountPoint))))
    throw new InvalidOperationException("Unexpected nested mount under the managed source");
  }
 }
 public static async Task Check(AgentOptions options,ManagedSourceBinding binding,CancellationToken ct)
 {
  if(binding.Proxmox is not null){await ProxmoxWorkloads.Prepare(options,binding,ct);return;}
  AuthorizeFiles(options,binding.Sources);
  var requirements=binding.Mounts.Concat(options.RequiredSourceMounts.Where(m=>binding.Sources.Any(p=>Overlaps(m.MountPoint,p)))).Distinct().ToArray();
  if(requirements.Length>0)ValidateMounts(binding.Sources,requirements,await ReadMounts(ct));
  foreach(var source in binding.Sources)
  {
   if(!Directory.Exists(source))throw new InvalidOperationException("Managed source directory is unavailable");
   if(options.AllowProxmoxSnapshots||requirements.Length>0)RejectLinks(source);
  }
 }
 public static void RejectFileLinks(string path)
 {
  var entry=File.Exists(path)?(FileSystemInfo)new FileInfo(path):new DirectoryInfo(path);
  if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Image archive tree contains a link");
  RejectLinks(Path.GetDirectoryName(path)!);
 }
 public static string Executable(AgentOptions options)
 {
  var executable=options.SourceCheckExecutable??Path.Combine(AppContext.BaseDirectory,OperatingSystem.IsWindows()?"DariaTech.Agent.exe":"DariaTech.Agent");
  if(Path.GetFileName(executable)!=(OperatingSystem.IsWindows()?"DariaTech.Agent.exe":"DariaTech.Agent"))throw new InvalidOperationException("Source check must use the installed native agent");
  if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Source preflight requires a native Unix agent");
  UnixPrivatePaths.AgentInstallation(executable);return executable;
 }
 public static string Hook(AgentOptions options,Guid job,long revision)
 {
  if(options.ConfigurationFile is null||!Path.IsPathFullyQualified(options.ConfigurationFile))throw new InvalidOperationException("Source preflight requires a protected service configuration");
  if(OperatingSystem.IsWindows())RestoreRootSecurity.Validate(Path.GetDirectoryName(options.ConfigurationFile)!);else UnixPrivatePaths.File(options.ConfigurationFile);
  if(OperatingSystem.IsWindows())throw new InvalidOperationException("Unix mount guards require Unix; Windows uses absolute UNC sources");
  var executable=Executable(options);
  static string Quote(string input)
  {
   if(input.Any(c=>char.IsControl(c)||c=='"'))throw new InvalidOperationException("Unsafe preflight path");
   return "\""+input+"\"";
  }
  return Quote(executable)+" --check-managed-source "+Quote(options.ConfigurationFile)+" "+job.ToString("D")+" "+revision;
 }
 public static AgentOptions ReadOptions(string path)
 {
  if(!Path.IsPathFullyQualified(path))throw new InvalidOperationException("Absolute service configuration required");
  if(OperatingSystem.IsWindows())RestoreRootSecurity.Validate(Path.GetDirectoryName(path)!);else UnixPrivatePaths.File(path);
  using var config=JsonDocument.Parse(File.ReadAllText(path));var options=config.RootElement.GetProperty("Agent").Deserialize<AgentOptions>()??throw new InvalidOperationException("Invalid agent configuration");
  options.ConfigurationFile=path;options.Validate();return options;
 }
 public static string BindingKey(Guid job,long revision)=>job.ToString("D")+":"+revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
 public static void Bind(ProtectedState state,ManagedSourceBinding binding)
 {
  var bindings=state.Read<Dictionary<string,ManagedSourceBinding>>("source-bindings.bin")??[];
  if(bindings.Count>=20000&&!bindings.ContainsKey(BindingKey(binding.JobId,binding.Revision)))throw new InvalidOperationException("Source-check journal requires local retention review");
  bindings[BindingKey(binding.JobId,binding.Revision)]=binding;state.Write("source-bindings.bin",bindings);
 }
}
