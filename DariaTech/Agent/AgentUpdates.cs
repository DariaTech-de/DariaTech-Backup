using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DariaTech.Contracts;
namespace DariaTech.Agent;

public sealed record UpdateJournal(Guid DeploymentId,AgentUpdateManifest Manifest,string Status,long HighestSequence,DateTimeOffset? ApplyingSince=null);
public static class AgentUpdates
{
 public static async Task Synchronize(HttpClient console,DuplicatiAdapter adapter,AgentOptions options,ProtectedState state,CancellationToken ct)
 {
  if(!options.AllowAgentUpdates)return;
  var journal=state.Read<UpdateJournal>("update.bin");
  if(journal is {Status:"Applying"})
  {
   var installed=options.Version==journal.Manifest.Version;
   if(!installed&&journal.ApplyingSince is {} started&&DateTimeOffset.UtcNow<started.AddMinutes(30))return;
   await Report(console,journal.DeploymentId,new(installed?"Installed":"Failed",installed?null:"InstallationIndeterminate"),ct);
   state.Write("update.bin",journal with{Status=installed?"Installed":"Failed"});return;
  }
  if(journal is {Status:"Downloaded"})
  {
   if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Automatic installer updates require Windows");
   // Never kill a backup/restore to install an update, including queued tasks.
   if(await adapter.HasPendingTasks(ct))return;
   using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(options.UpdatePublicKeyFile!));
   // Validate the stored manifest again against current time and rehash staged bytes immediately before execution.
   if(!UpdateProtocol.Valid(journal.Manifest,DateTimeOffset.UtcNow)||!Version.TryParse(options.Version,out var installed)||Version.Parse(journal.Manifest.Version)<=installed)
   {await Report(console,journal.DeploymentId,new("Rejected","UpdateRejected"),ct);state.Write("update.bin",journal with{Status="Rejected"});return;}
   var artifact=Path.Combine(options.StateDirectory,"updates",$"{journal.Manifest.ReleaseId:D}.exe");
   await VerifyArtifact(artifact,journal.Manifest,ct);
   if(ArtifactVersion(artifact)!=journal.Manifest.Version)throw new CryptographicException("Installer version differs from signed manifest");
   await Report(console,journal.DeploymentId,new("Downloaded",null),ct);
   state.Write("update.bin",journal with{Status="Applying",ApplyingSince=DateTimeOffset.UtcNow});
   await Report(console,journal.DeploymentId,new("Applying",null),ct);
   // The verified Inno Setup process is independent of the managed-engine kill-on-close job.
   // It stops/replaces/restarts the service; the new process confirms its actual installed version.
   var start=new ProcessStartInfo(artifact){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=options.StateDirectory};
   foreach(var arg in new[]{"/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-","/console="+options.ConsoleUrl,"/DIR="+AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)})start.ArgumentList.Add(arg);
   using var process=Process.Start(start)??throw new InvalidOperationException("Installer could not start");return;
  }
  using var response=await console.GetAsync("/api/v1/agent/update",ct);if(response.StatusCode==HttpStatusCode.NoContent)return;response.EnsureSuccessStatusCode();
  var assignment=await response.Content.ReadFromJsonAsync<UpdateAssignment>(ct)??throw new InvalidOperationException("Missing update assignment");
  using var trust=ECDsa.Create();trust.ImportFromPem(File.ReadAllText(options.UpdatePublicKeyFile!));
  AgentUpdateManifest manifest;
  try{manifest=UpdateProtocol.Verify(assignment.Manifest,trust,DateTimeOffset.UtcNow);}
  catch(Exception e)when(e is CryptographicException or FormatException or System.Text.Json.JsonException){await Report(console,assignment.DeploymentId,new("Rejected","UpdateRejected"),ct);return;}
  if(!UpdateProtocol.Newer(manifest,options.Version,journal?.HighestSequence??0)||!OperatingSystem.IsWindows())
  {await Report(console,assignment.DeploymentId,new("Rejected","UpdateRejected"),ct);return;}
  if(await adapter.HasPendingTasks(ct))return;
  var folder=Path.Combine(options.StateDirectory,"updates");Directory.CreateDirectory(folder);
  if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Unsafe update staging directory");
  var file=Path.Combine(folder,$"{manifest.ReleaseId:D}.exe");
  try
  {
   await Download(manifest,file,options.UpdateDownloadHosts,ct);await VerifyArtifact(file,manifest,ct);
   if(manifest.Expires<=DateTimeOffset.UtcNow)throw new CryptographicException("Manifest expired while downloading");
   state.Write("update.bin",new UpdateJournal(assignment.DeploymentId,manifest,"Downloaded",manifest.Sequence));
   await Report(console,assignment.DeploymentId,new("Downloaded",null),ct);
  }
  catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
  catch(Exception){File.Delete(file);await Report(console,assignment.DeploymentId,new("Failed","DownloadFailed"),ct);}
 }
 private static async Task Report(HttpClient console,Guid id,UpdateReceipt receipt,CancellationToken ct)
 {using var response=await console.PostAsJsonAsync($"/api/v1/agent/updates/{id}/receipt",receipt,ct);response.EnsureSuccessStatusCode();}
 public static string ArtifactVersion(string path)
 {
  var v=FileVersionInfo.GetVersionInfo(path);
  // Inno may format the textual FileVersion with three components; compare the signed four-component PE version.
  return new Version(v.FileMajorPart,v.FileMinorPart,v.FileBuildPart,v.FilePrivatePart).ToString();
 }
 public static async Task VerifyArtifact(string path,AgentUpdateManifest manifest,CancellationToken ct)
 {
  await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
  if(file.Length!=manifest.Length)throw new CryptographicException("Update artifact size mismatch");
  var hash=await SHA256.HashDataAsync(file,ct);
  if(!CryptographicOperations.FixedTimeEquals(hash,Convert.FromHexString(manifest.Sha256)))throw new CryptographicException("Update artifact hash mismatch");
 }
 public static bool AllowedDownload(Uri url,string[] hosts)=>url.Scheme=="https"&&url.IsDefaultPort&&url.UserInfo==""&&url.Fragment==""&&hosts.Contains(url.DnsSafeHost,StringComparer.OrdinalIgnoreCase)&&!IPAddress.TryParse(url.DnsSafeHost,out _);
 private static async Task Download(AgentUpdateManifest manifest,string path,string[] hosts,CancellationToken ct)
 {
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(15));
  using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
  var url=new Uri(manifest.ArtifactUrl);
  for(var redirects=0;redirects<=5;redirects++)
  {
   if(!AllowedDownload(url,hosts))throw new InvalidOperationException("Unapproved download host");
   using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
   if((int)response.StatusCode is >=300 and <400){url=new Uri(url,response.Headers.Location??throw new HttpRequestException("Missing redirect"));continue;}
   response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength is {} length&&length!=manifest.Length)throw new CryptographicException("Unexpected artifact size");
   var temporary=path+".partial";
   try
   {
    await using var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None);await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);
    var buffer=new byte[65536];long received=0;int read;
    while((read=await input.ReadAsync(buffer,timeout.Token))>0){received+=read;if(received>manifest.Length)throw new CryptographicException("Artifact exceeds signed length");await output.WriteAsync(buffer.AsMemory(0,read),timeout.Token);}
    await output.FlushAsync(timeout.Token);output.Flush(true);
   }
   catch{File.Delete(temporary);throw;}
   File.Move(temporary,path,true);return;
  }
  throw new HttpRequestException("Too many artifact redirects");
 }
}
public sealed partial class DuplicatiAdapter
{
 public async Task<bool> HasPendingTasks(CancellationToken ct)
 {
  using var response=await client.GetAsync("/api/v1/tasks",ct);response.EnsureSuccessStatusCode();using var data=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
  return data.RootElement.EnumerateArray().Any(x=>Get(x,"Status").GetString() is "Running" or "Waiting");
 }
}
