using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using DariaTech.Agent;
using DariaTech.Contracts;

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
 catch(Exception error){System.Console.Error.WriteLine($"Engine installation check rejected ({error.GetType().Name})");Environment.ExitCode=2;}
 return;
}

if(args.Length==2&&args[0]=="--check-restore-root")
{
 try{DuplicatiAdapter.RestoreDestination(args[1],"ownership-diagnostic-"+Guid.NewGuid().ToString("N"));System.Console.WriteLine("Restore root permissions verified");}
 catch(Exception error){System.Console.Error.WriteLine($"Restore root check rejected ({error.GetType().Name})");Environment.ExitCode=2;}
 return;
}

var builder=Host.CreateApplicationBuilder(new HostApplicationBuilderSettings{Args=args,ContentRootPath=AppContext.BaseDirectory});var options=builder.Configuration.GetSection("Agent").Get<AgentOptions>()??new();options.Validate();var state=new ProtectedState(options);
if(args.Contains("enroll"))
{
 if(state.Read<AgentIdentity>("identity.bin") is not null)throw new InvalidOperationException("Agent is already enrolled");
 var index=Array.IndexOf(args,"--token-file");if(index<0||index+1>=args.Length)throw new InvalidOperationException("enroll requires --token-file");
 var token=File.ReadAllText(args[index+1]).Trim();
 using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(options.ConsoleUrl),Timeout=TimeSpan.FromSeconds(30)};
 using var response=await client.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(token,Environment.MachineName,RuntimeInformation.OSDescription,options.Version));response.EnsureSuccessStatusCode();
 var identity=await response.Content.ReadFromJsonAsync<EnrollmentResponse>()??throw new InvalidOperationException("Invalid enrollment response");state.Write("identity.bin",new AgentIdentity(identity.DeviceId,identity.Credential));
 System.Console.WriteLine($"Registered device {identity.DeviceId}");return;
}
if(args.Contains("set-engine-credential"))
{
 var index=Array.IndexOf(args,"--credential-file");if(index<0||index+1>=args.Length)throw new InvalidOperationException("--credential-file is required");
 state.Write("engine-credential.bin",File.ReadAllText(args[index+1]).TrimEnd('\r','\n'));System.Console.WriteLine("Engine credential protected for this service identity");return;
}
var brand=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"product.json"))).RootElement;
builder.Services.AddWindowsService(o=>o.ServiceName=brand.GetProperty("windowsServiceName").GetString()!);
builder.Services.AddSingleton(options);builder.Services.AddSingleton(state);
if(options.ManageEngine)builder.Services.AddHostedService<ManagedEngine>();
builder.Services.AddHostedService<Worker>();await builder.Build().RunAsync();
