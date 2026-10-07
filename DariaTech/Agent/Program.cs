using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using DariaTech.Agent;
using DariaTech.Contracts;

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
