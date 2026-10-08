using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed record ProxmoxImportState(Guid CommandId,Guid EngineInstance,long TaskId,ProxmoxRestoreSelection Selection,ManagedSourceBinding Binding,
 string Destination,string Stage,DateTimeOffset Updated,int? ProcessId=null,long? ProcessStart=null,bool CleanupRequired=false);

public static class ProxmoxRestore
{
 public static string StateFile(Guid command)=>"proxmox-import-"+command.ToString("N")+".bin";
 public static void Authorize(AgentOptions options,ManagedSourceBinding binding,ProxmoxRestoreSelection selection)
 {
  if(!OperatingSystem.IsLinux()||!options.AllowProxmoxRestore||!options.AllowRemoteCommands||!options.AllowProxmoxSnapshots||
     binding.Proxmox is null||binding.Revision!=selection.ConfigurationRevision||
     !options.AllowedProxmoxRestoreGuestIds.Contains(selection.TargetGuestId)||!options.AllowedProxmoxRestoreStorages.Contains(selection.Storage,StringComparer.Ordinal)||
     !ProxmoxPolicy.ValidRestore(selection,DateTimeOffset.UtcNow))throw new InvalidOperationException("Guest-image restore is not locally authorized");
  var guest=ProxmoxWorkloads.ArchiveGuest(selection.ArchivePath);
  if(guest is null||!binding.Proxmox.GuestIds.Contains(guest.Value.Id)||!options.AllowedProxmoxGuestIds.Contains(guest.Value.Id)||
     binding.Sources.Length!=1||Path.GetDirectoryName(Path.GetFullPath(selection.ArchivePath))!=Path.GetFullPath(binding.Sources[0]).TrimEnd('/'))
   throw new InvalidOperationException("Restore archive is outside the applied guest-image source");
  if(GuestExists(selection.TargetGuestId))throw new InvalidOperationException("Restore target guest ID already exists");
  if(options.RestoreRoot is null)throw new InvalidOperationException("Protected local restore root required");
  RestoreRootSecurity.Validate(options.RestoreRoot);
 }
 private static bool GuestExists(int id)
 {
  if(!Directory.Exists("/etc/pve/nodes"))throw new InvalidOperationException("Proxmox cluster filesystem is unavailable");
  return Directory.EnumerateFiles("/etc/pve/nodes",id+".conf",SearchOption.AllDirectories).Any();
 }
 public static async Task Launch(AgentOptions options,ProtectedState state,ProxmoxImportState import,CancellationToken ct)
 {
  if(!OperatingSystem.IsLinux()||UnixPrivatePaths.EffectiveUserId!=0||options.ConfigurationFile is null)throw new InvalidOperationException("Native root service required for guest import");
  var executable=SourceChecks.Executable(options);
  Authorize(options,import.Binding,import.Selection);
  var launching=import with{Stage="Launching",Updated=DateTimeOffset.UtcNow};state.Write(StateFile(import.CommandId),launching);
  var info=new ProcessStartInfo("/usr/bin/systemd-run"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(var arg in new[]{"--quiet","--collect","--unit="+UnixServices.ServiceName+"-restore-"+import.CommandId.ToString("N"),
    "--property=Type=exec","--property=User=root","--property=UMask=0077","--property=KillMode=control-group","--",executable,"--import-proxmox",options.ConfigurationFile,import.CommandId.ToString("D")})info.ArgumentList.Add(arg);
  try
  {
   using var process=Process.Start(info)??throw new InvalidOperationException("Independent guest import could not start");
   var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);
   await process.WaitForExitAsync(ct);await output;await error;
   if(process.ExitCode!=0)throw new InvalidOperationException("Independent guest import could not start");
  }
  catch
  {
   // Do not relaunch after an uncertain service-control result.
   if(state.Read<ProxmoxImportState>(StateFile(import.CommandId)) is {Stage:"Launching"} current)state.Write(StateFile(import.CommandId),current with{Stage="Indeterminate",Updated=DateTimeOffset.UtcNow});
   throw;
  }
 }
 public static CommandReceipt? IndependentReceipt(ProtectedState state,Guid command)
 {
  var import=state.Read<ProxmoxImportState>(StateFile(command));
  if(import is null||import.Stage=="Restoring")return null;
  if(import.Stage=="Launching"&&import.Updated<DateTimeOffset.UtcNow.AddMinutes(-2)||import.Stage=="Running"&&!ProcessAlive(import))
  {
   import=import with{Stage="Indeterminate",Updated=DateTimeOffset.UtcNow};state.Write(StateFile(command),import);
  }
  return import.Stage switch
  {
   "Completed"=>new("Completed",import.TaskId,null),
   "Failed"=>new("Failed",import.TaskId,"EngineTaskFailed"),
   "Indeterminate"=>new("Indeterminate",import.TaskId,"DispatchIndeterminate"),
   _=>new("Accepted",import.TaskId,null)
  };
 }
 private static bool ProcessAlive(ProxmoxImportState import)
 {
  if(import.ProcessId is not {} id||import.ProcessStart is not {} started)return false;
  try{using var process=Process.GetProcessById(id);return !process.HasExited&&process.StartTime.ToUniversalTime().Ticks==started;}
  catch(ArgumentException){return false;}
  catch(InvalidOperationException){return false;}
 }
 public static bool Active(ProtectedState state,string directory)=>Directory.EnumerateFiles(directory,"proxmox-import-*.bin")
  .Any(path=>state.Read<ProxmoxImportState>(Path.GetFileName(path)) is {Stage:"Launching" or "Running"});
 public static async Task Apply(string config,Guid command,CancellationToken ct)
 {
  if(!OperatingSystem.IsLinux()||UnixPrivatePaths.EffectiveUserId!=0)throw new InvalidOperationException("Proxmox import requires the local root service");
  var options=SourceChecks.ReadOptions(config);var state=new ProtectedState(options);var file=StateFile(command);
  var lockPath=Path.Combine(options.StateDirectory,"proxmox-import-"+command.ToString("N")+".lock");if(File.Exists(lockPath))UnixPrivatePaths.File(lockPath);
  using var executionLock=new FileStream(lockPath,new FileStreamOptions{Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});
  var import=state.Read<ProxmoxImportState>(file)??throw new InvalidOperationException("Import journal missing");
  if(import.CommandId!=command||import.Stage!="Launching")throw new InvalidOperationException("Import replay rejected");
  using var self=Process.GetCurrentProcess();var running=import with{Stage="Running",Updated=DateTimeOffset.UtcNow,ProcessId=self.Id,ProcessStart=self.StartTime.ToUniversalTime().Ticks};
  state.Write(file,running);
  try
  {
   // The command was valid at dispatch. The archive/import may legitimately take
   // longer than the signed command's delivery window.
   Authorize(options,import.Binding,import.Selection);
   UnixPrivatePaths.Directory(import.Destination,false);
   var entries=Directory.EnumerateFileSystemEntries(import.Destination,"*",SearchOption.AllDirectories).Take(10001).ToArray();
   if(entries.Length>10000)throw new InvalidOperationException("Unexpected restored image tree");
   foreach(var entry in entries)SourceChecks.RejectFileLinks(entry);
   var files=entries.Where(File.Exists).ToArray();
   if(files.Length!=1||Path.GetFileName(files[0])!=Path.GetFileName(import.Selection.ArchivePath))throw new InvalidOperationException("Exactly the selected guest image must be restored");
   var archive=files[0];UnixPrivatePaths.TrustedFile(archive);var guest=ProxmoxWorkloads.ArchiveGuest(archive)??throw new InvalidOperationException("Invalid native image archive");
   using(var input=File.OpenRead(archive)){var magic=new byte[4];if(input.Length<24||input.Read(magic)!=4||!magic.AsSpan().SequenceEqual(new byte[]{0x28,0xb5,0x2f,0xfd}))throw new InvalidOperationException("Incomplete native image archive");}
   var executable=guest.Type=="qemu"?"/usr/sbin/qmrestore":"/usr/sbin/pct";
   if(!File.Exists(executable))executable=guest.Type=="qemu"?"/usr/bin/qmrestore":"/usr/bin/pct";
   UnixPrivatePaths.TrustedFile(executable);
   var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
   if(guest.Type=="qemu")
   {
    info.ArgumentList.Add(archive);info.ArgumentList.Add(import.Selection.TargetGuestId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    info.ArgumentList.Add("--unique");info.ArgumentList.Add("1");
   }
   else
   {
    info.ArgumentList.Add("restore");info.ArgumentList.Add(import.Selection.TargetGuestId.ToString(System.Globalization.CultureInfo.InvariantCulture));info.ArgumentList.Add(archive);
   }
   info.ArgumentList.Add("--storage");info.ArgumentList.Add(import.Selection.Storage);
   // Never use --force, never start a recovered guest automatically.
   using var process=Process.Start(info)??throw new InvalidOperationException("Native guest import could not start");
   var stdout=Discard(process.StandardOutput,ct);var stderr=Discard(process.StandardError,ct);
   try{await process.WaitForExitAsync(ct);await Task.WhenAll(stdout,stderr);}
   finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync(CancellationToken.None);}}
   if(process.ExitCode!=0||!GuestExists(import.Selection.TargetGuestId))throw new InvalidOperationException("Native guest import did not complete");
   state.Write(file,running with{Stage="Completed",Updated=DateTimeOffset.UtcNow});
   // The new stopped guest owns its disks; the unencrypted temporary archive is no longer needed.
   try{Directory.Delete(import.Destination,true);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){state.Write(file,running with{Stage="Completed",Updated=DateTimeOffset.UtcNow,CleanupRequired=true});}
  }
  catch{state.Write(file,running with{Stage="Failed",Updated=DateTimeOffset.UtcNow});throw;}
 }
 private static async Task Discard(StreamReader reader,CancellationToken ct)
 {var buffer=new char[8192];while(await reader.ReadAsync(buffer,ct)>0){}}
}
public sealed partial class DuplicatiAdapter
{
 public async Task<long> RestoreProxmox(DeviceCommand command,CancellationToken ct)
 {
  var selection=command.ProxmoxRestore??throw new InvalidOperationException("Guest-image restore selection missing");
  var key=(state.Read<Dictionary<string,string>>("source-jobs.bin")??[]).GetValueOrDefault(command.LocalJobId);
  var binding=key is null?null:(state.Read<Dictionary<string,ManagedSourceBinding>>("source-bindings.bin")??[]).GetValueOrDefault(key);
  if(binding is null)throw new InvalidOperationException("Applied guest-image configuration missing");
  ProxmoxRestore.Authorize(agentOptions,binding,selection);
  var destination=RestoreDestination(agentOptions.RestoreRoot,"proxmox-"+command.Id.ToString("N"));
  using var response=await client.PostAsJsonAsync($"/api/v1/backup/{command.LocalJobId}/restore",
   new{paths=new[]{selection.ArchivePath},time=selection.Snapshot.ToUniversalTime().ToString("O"),restore_path=destination,overwrite=false,permissions=false,skip_metadata=true},ct);
  response.EnsureSuccessStatusCode();using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  if(!long.TryParse(Get(result.RootElement,"ID").ToString(),out var task)||task<1)throw new HttpRequestException("Invalid native restore task receipt");
  var import=new ProxmoxImportState(command.Id,state.Read<Guid>("engine-instance.bin"),task,selection,binding,destination,"Restoring",DateTimeOffset.UtcNow);
  state.Write(ProxmoxRestore.StateFile(command.Id),import);return task;
 }
 public async Task<CommandReceipt> ProxmoxReceipt(Guid command,long task,CancellationToken ct)
 {
  if(ProxmoxRestore.IndependentReceipt(state,command) is {} independent)return independent;
  var import=state.Read<ProxmoxImportState>(ProxmoxRestore.StateFile(command))??throw new InvalidOperationException("Import journal missing");
  if(import.TaskId!=task||import.EngineInstance!=state.Read<Guid>("engine-instance.bin"))return new("Indeterminate",task,"DispatchIndeterminate");
  var native=await TaskReceipt(task,ct);
  if(native.Status=="Accepted")return native;
  if(native.Status!="Completed"){state.Write(ProxmoxRestore.StateFile(command),import with{Stage=native.Status=="Indeterminate"?"Indeterminate":"Failed",Updated=DateTimeOffset.UtcNow});return native;}
  if(import.EngineInstance!=state.Read<Guid>("engine-instance.bin"))return new("Indeterminate",task,"DispatchIndeterminate");
  try{await ProxmoxRestore.Launch(agentOptions,state,import,ct);return new("Accepted",task,null);}
  catch(InvalidOperationException){return new("Indeterminate",task,"DispatchIndeterminate");}
 }
}
