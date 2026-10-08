using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using DariaTech.Agent;
using DariaTech.Contracts;

if(args.Length==4&&args[0]=="--check-managed-source")
{
 if(!string.Equals(Environment.GetEnvironmentVariable("DUPLICATI__OPERATIONNAME"),"Backup",StringComparison.OrdinalIgnoreCase))return;
 try
 {
  var sourceOptions=SourceChecks.ReadOptions(args[1]);var sourceState=new ProtectedState(sourceOptions);
  var job=Guid.Parse(args[2]);var revision=long.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture);
  var binding=(sourceState.Read<Dictionary<string,ManagedSourceBinding>>("source-bindings.bin")??[]).GetValueOrDefault(SourceChecks.BindingKey(job,revision))??throw new InvalidOperationException("Source revision not found");
  using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(binding.Proxmox is null?1:120));
  await SourceChecks.Check(sourceOptions,binding,deadline.Token);
 }
 catch(Exception error){System.Console.Error.WriteLine($"Managed source preflight failed ({error.GetType().Name}); no backup was started.");Environment.ExitCode=2;}
 return;
}

if(args.Length==3&&args[0]=="--apply-unix-update")
{
 try{using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));await UnixUpdates.Apply(args[1],args[2],deadline.Token);}
 catch(Exception error){System.Console.Error.WriteLine($"Verified Unix update failed ({error.GetType().Name}); retained state and previous binaries are available.");Environment.ExitCode=2;}
 return;
}
if(args.Length==1&&args[0]=="--service-name"){System.Console.WriteLine(UnixServices.ServiceName);return;}
if(args.Length==1&&args[0]=="--service-label"){System.Console.WriteLine(UnixServices.MacLabel);return;}
if(args.Length==2&&args[0]=="--write-service-file"){UnixServices.Write(args[1]);return;}

if(args.Length>0&&args[0]=="--initialize")
{
 try{var provisioned=UnixProvisioning.Initialize(args);System.Console.WriteLine("Agent service configuration initialized; enrollment completes on service startup.");}
 catch(Exception error){System.Console.Error.WriteLine($"Agent initialization rejected ({error.GetType().Name}); verify protected input files and local options.");Environment.ExitCode=2;}
 return;
}

// Read-only local diagnostic: connect without sending HTTP data or secrets.
if(args.Length==4&&args[0]=="--check-engine-port")
{
 try
 {
  using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
  using var probe=await OwnedEngineConnection.Connect(new(int.Parse(args[1]),long.Parse(args[2])),int.Parse(args[3]),timeout.Token);
  System.Console.WriteLine("Engine connection owner verified");
 }
 catch(Exception error){System.Console.Error.WriteLine($"Engine ownership check rejected ({error.GetType().Name})");Environment.ExitCode=2;}
 return;
}

if(args.Length==2&&args[0]=="--check-engine-installation")
{
 try{EngineInstallation.ValidateExternal(args[1]);System.Console.WriteLine("External engine installation permissions verified");}
 catch(Exception error){System.Console.Error.WriteLine($"Engine installation check rejected ({error.GetType().Name})"+(error is InvalidOperationException?": "+error.Message:""));Environment.ExitCode=2;}
 return;
}

if(args.Length==2&&args[0]=="--check-restore-root")
{
 try{DuplicatiAdapter.RestoreDestination(args[1],"ownership-diagnostic-"+Guid.NewGuid().ToString("N"));System.Console.WriteLine("Restore root permissions verified");}
 catch(Exception error){System.Console.Error.WriteLine($"Restore root check rejected ({error.GetType().Name})");Environment.ExitCode=2;}
 return;
}

var builder=Host.CreateApplicationBuilder(new HostApplicationBuilderSettings{Args=args,ContentRootPath=AppContext.BaseDirectory});
var configIndex=Array.IndexOf(args,"--agent-config");
if(configIndex>=0)
{
 if(configIndex+1>=args.Length||!Path.IsPathFullyQualified(args[configIndex+1]))throw new InvalidOperationException("Absolute --agent-config required");
 if(!OperatingSystem.IsWindows())UnixPrivatePaths.File(args[configIndex+1]);
 builder.Configuration.AddJsonFile(args[configIndex+1],optional:false,reloadOnChange:false);
}
var options=builder.Configuration.GetSection("Agent").Get<AgentOptions>()??new();if(configIndex>=0)options.ConfigurationFile=args[configIndex+1];options.Validate();var state=new ProtectedState(options);
if(args.Contains("enroll"))
{
 if(state.Read<AgentIdentity>("identity.bin") is not null)throw new InvalidOperationException("Agent is already enrolled");
 var index=Array.IndexOf(args,"--token-file");if(index<0||index+1>=args.Length)throw new InvalidOperationException("enroll requires --token-file");
 if(!OperatingSystem.IsWindows())UnixPrivatePaths.File(args[index+1]);
 var token=File.ReadAllText(args[index+1]).Trim();
 using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(options.ConsoleUrl),Timeout=TimeSpan.FromSeconds(30)};
 using var response=await client.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(token,Environment.MachineName,RuntimeInformation.OSDescription,options.Version,options.Platform));response.EnsureSuccessStatusCode();
 var identity=await response.Content.ReadFromJsonAsync<EnrollmentResponse>()??throw new InvalidOperationException("Invalid enrollment response");state.Write("identity.bin",new AgentIdentity(identity.DeviceId,identity.Credential));
 System.Console.WriteLine($"Registered device {identity.DeviceId}");return;
}
if(args.Contains("set-engine-credential"))
{
 var index=Array.IndexOf(args,"--credential-file");if(index<0||index+1>=args.Length)throw new InvalidOperationException("--credential-file is required");
 if(!OperatingSystem.IsWindows())UnixPrivatePaths.File(args[index+1]);
 state.Write("engine-credential.bin",File.ReadAllText(args[index+1]).TrimEnd('\r','\n'));System.Console.WriteLine("Engine credential protected for this service identity");return;
}
var brand=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"product.json"))).RootElement;
if(OperatingSystem.IsWindows())builder.Services.AddWindowsService(o=>o.ServiceName=brand.GetProperty("windowsServiceName").GetString()!);
builder.Services.AddSingleton(options);builder.Services.AddSingleton(state);
if(options.ManageEngine)builder.Services.AddHostedService<ManagedEngine>();
builder.Services.AddHostedService<Worker>();await builder.Build().RunAsync();
