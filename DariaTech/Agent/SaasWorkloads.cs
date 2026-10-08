using System.Net.Http.Json;
using System.Text.Json;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed record SaasJobBinding(Guid JobId,long Revision,string MountPoint,SaasSource Source);
public static class SaasWorkloads
{
 public static bool DevelopmentTestingAllowed=>Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")=="Development"&&Environment.GetEnvironmentVariable("DARIATECH_SAAS_TESTS")=="1";
 public static void Authorize(AgentOptions options,SaasSource source,bool restore=false)
 {
  if(!options.AllowSaasWorkloads||!options.ManageEngine||!options.AllowedSaasTenants.Contains(source.DirectoryTenant,StringComparer.OrdinalIgnoreCase)||
     restore&&!options.AllowSaasRestore)throw new InvalidOperationException("Cloud workload is not locally authorized");
  if(options.AllowUnlicensedSaasDevelopment&&!DevelopmentTestingAllowed)throw new InvalidOperationException("Development-only SaaS opt-in refused");
 }
}
public sealed partial class DuplicatiAdapter
{
 public async Task RequireSaasProvider(SaasSource source,bool restore,CancellationToken ct)
 {
  SaasWorkloads.Authorize(agentOptions,source,restore);
  using var response=await client.GetAsync("/api/v1/systeminfo",ct);response.EnsureSuccessStatusCode();
  using var info=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  var modules=Get(info.RootElement,restore?"RestoreDestinationProviderModules":"SourceProviderModules");
  var key=SaasPolicy.Key(source.Provider);
  if(modules.ValueKind!=JsonValueKind.Array||!modules.EnumerateArray().Any(x=>Get(x,"Key").ToString()==key))throw new InvalidOperationException("Required native SaaS provider is not installed");
  var license=Get(info.RootElement,"LocalLicenseStatus");
  if(!(agentOptions.AllowUnlicensedSaasDevelopment&&SaasWorkloads.DevelopmentTestingAllowed)&&
     (Get(license,"IsConfigured").ValueKind!=JsonValueKind.True||Get(license,"IsValid").ValueKind!=JsonValueKind.True))
   throw new InvalidOperationException("A valid local SaaS subscription is required for production");
 }
 private void BindSaasJob(string id,ConfigurationAssignment assignment,string mountPoint)
 {
  var bindings=state.Read<Dictionary<string,SaasJobBinding>>("saas-bindings.bin")??[];
  if(assignment.Definition.Saas is {} source)bindings[id]=new(assignment.JobId,assignment.Revision,mountPoint,source);
  else bindings.Remove(id);
  state.Write("saas-bindings.bin",bindings);
 }
 public async Task<long> RestoreSaas(DeviceCommand command,CancellationToken ct)
 {
  var selection=command.SaasRestore??throw new InvalidOperationException("SaaS restore selection missing");
  var bindings=state.Read<Dictionary<string,SaasJobBinding>>("saas-bindings.bin")??[];
  if(!bindings.TryGetValue(command.LocalJobId,out var binding)||binding.Revision!=selection.ConfigurationRevision||
     !SaasPolicy.ValidRestore(selection,DateTimeOffset.UtcNow))throw new InvalidOperationException("SaaS revision or selection rejected");
  SaasWorkloads.Authorize(agentOptions,binding.Source,true);
  if(selection.Paths.Any(p=>!InsideVirtualMount(p,binding.MountPoint)))throw new InvalidOperationException("Restore paths are outside the managed cloud source");
  await RequireSaasProvider(binding.Source,true,ct);
  // Credentials are already in this job's encrypted engine settings; never copy them
  // into a command, a provider URL, command arguments or telemetry.
  var target="@"+SaasPolicy.Key(binding.Source.Provider)+":///"+selection.TargetPath;
  using var response=await client.PostAsJsonAsync($"/api/v1/backup/{command.LocalJobId}/restore",
   new{paths=selection.Paths,time=selection.Snapshot.ToUniversalTime().ToString("O"),restore_path=target,overwrite=true,permissions=false,skip_metadata=false},ct);
  response.EnsureSuccessStatusCode();using var result=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  if(!long.TryParse(Get(result.RootElement,"ID").ToString(),out var id)||id<1)throw new HttpRequestException("Invalid native restore task receipt");
  return id;
 }
 public static bool InsideVirtualMount(string path,string mountPoint)
 {
  try
  {
   var root=Path.GetFullPath(mountPoint).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   return Path.IsPathFullyQualified(path)&&Path.GetFullPath(path).StartsWith(root,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
  }catch(ArgumentException){return false;}
 }
}
