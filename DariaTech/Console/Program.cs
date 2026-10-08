using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using DariaTech.Console.Api;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using DariaTech.Console.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

if(args.Contains("--healthcheck"))
{
 using var client=new HttpClient();
 return (await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode?0:1;
}
var builder=WebApplication.CreateBuilder(args);
if(builder.Configuration["Database:ConnectionFile"] is {} connectionFile)builder.Configuration["ConnectionStrings:Management"]=File.ReadAllText(connectionFile).Trim();
builder.WebHost.ConfigureKestrel(o=>{o.Limits.MaxRequestBodySize=256*1024;o.AddServerHeader=false;});
builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;o.SerializerOptions.RespectNullableAnnotations=true;o.SerializerOptions.RespectRequiredConstructorParameters=true;});
builder.Services.AddHttpContextAccessor();builder.Services.AddScoped<TenantScope>();
builder.Services.AddDbContext<ManagementDb>(o=>o.UseNpgsql(builder.Configuration.GetConnectionString("Management")??throw new InvalidOperationException("Management database connection is required")));
builder.Services.AddSingleton<CommandSigning>();
builder.Services.AddSingleton<AgentReleases>();
builder.Services.AddSingleton<ISecretStore,EncryptedSecretStore>();
builder.Services.AddDataProtection().SetApplicationName("DariaTech.ManagedBackup").PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Security:KeyDirectory"]??"./keys"));
builder.Services.AddOptions<KeyManagementOptions>().Configure<ISecretStore>((o,s)=>o.XmlEncryptor=new KeyXmlEncryptor(s));
builder.Services.AddScoped<SessionEvents>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o=>
{
 o.LoginPath="/Login";o.Cookie.Name="DariaTech.Session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;
 o.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
 o.ExpireTimeSpan=TimeSpan.FromMinutes(30);o.SlidingExpiration=false;o.EventsType=typeof(SessionEvents);
});
builder.Services.AddAuthorization(o=>
{
 o.AddPolicy("Admin",p=>p.RequireRole("SuperAdmin","Administrator"));
 o.AddPolicy("Operator",p=>p.RequireRole("SuperAdmin","Administrator","Technician"));
});
builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-Token";o.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
builder.Services.AddRazorPages(o=>{o.Conventions.AuthorizeFolder("/");o.Conventions.AllowAnonymousToPage("/Login");o.Conventions.AllowAnonymousToPage("/Licenses");});
builder.Services.AddOptions<MonitoringOptions>().Bind(builder.Configuration.GetSection("Monitoring"))
 .Validate(o=>o.OfflineMinutes is >=1 and <=1440&&o.BackupAgeHours is >=1 and <=8760&&o.PollSeconds is >=10 and <=3600&&o.RepeatedFailureCount is >=2 and <=20&&o.QuotaCriticalPercent is >=1 and <=99&&o.QuotaWarningPercent>o.QuotaCriticalPercent&&o.QuotaWarningPercent<=99,"Invalid monitoring thresholds").ValidateOnStart();
builder.Services.AddHostedService<Monitoring>();
builder.Services.AddOptions<NotificationOptions>().Bind(builder.Configuration.GetSection("Notifications")).Validate(o=>o.Valid(),"Valid SMTP settings and encrypted password file required").ValidateOnStart();
builder.Services.AddSingleton<INotificationTransport,SmtpTransport>();builder.Services.AddHostedService<Notifications>();
builder.Services.AddRateLimiter(o=>
{
 o.RejectionStatusCode=429;
 o.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new(){PermitLimit=500,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
 o.AddPolicy("Login",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new(){PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
 o.AddPolicy("Enrollment",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new(){PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
 o.AddPolicy("Agent",c=>RateLimitPartition.GetFixedWindowLimiter((c.Connection.RemoteIpAddress?.ToString()??"unknown")+":"+c.Request.Headers["X-Device-Id"],_=>new(){PermitLimit=60,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
});
builder.Services.Configure<ForwardedHeadersOptions>(o=>
{
 o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;
 o.ForwardLimit=1;
 // Do not trust arbitrary X-Forwarded-* senders. Keep framework loopback defaults.
 foreach(var ip in (builder.Configuration["Security:TrustedProxies"]??"").Split(',',StringSplitOptions.RemoveEmptyEntries))o.KnownProxies.Add(IPAddress.Parse(ip.Trim()));
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealth>("postgresql");
var app=builder.Build();
if(args.Contains("--protect-smtp-password"))
{
 var input=builder.Configuration["Provision:PasswordFile"]??throw new InvalidOperationException("Provision:PasswordFile required");
 var output=builder.Configuration["Provision:OutputFile"]??throw new InvalidOperationException("Provision:OutputFile required");
 if(File.Exists(output))throw new InvalidOperationException("Refusing to overwrite existing SMTP password envelope");
 var password=File.ReadAllText(input).TrimEnd('\r','\n');if(password.Length<1||password.Length>2000)throw new InvalidOperationException("Invalid SMTP password length");
 File.WriteAllText(output,app.Services.GetRequiredService<ISecretStore>().Protect(Guid.Empty,"smtp-password",password));
 if(!OperatingSystem.IsWindows())File.SetUnixFileMode(output,UnixFileMode.UserRead|UnixFileMode.UserWrite);
 return 0;
}
if(args.Contains("--migrate")||args.Contains("--bootstrap-user")||args.Contains("--recover-user")||args.Contains("--erase-customer")||args.Contains("--prune-history"))
{
 using var s=app.Services.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Maintenance=true;
 var db=s.ServiceProvider.GetRequiredService<ManagementDb>();
 if(args.Contains("--erase-customer"))
 {
  if(!Guid.TryParse(builder.Configuration["Privacy:TenantId"],out var tenant))throw new InvalidOperationException("Privacy:TenantId required");
  await Privacy.EraseCustomer(db,tenant,builder.Configuration.GetValue<bool>("Privacy:Apply"));System.Console.WriteLine(builder.Configuration.GetValue<bool>("Privacy:Apply")?"Central customer erasure completed; audit pseudonymous references retained":"Dry run valid; no data erased. Set Privacy:Apply=true for deliberate erasure.");return 0;
 }
 if(args.Contains("--prune-history")){var counts=await Privacy.Prune(db,builder.Configuration.GetValue<int>("Privacy:RetentionDays",180),builder.Configuration.GetValue<bool>("Privacy:Apply"));System.Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{counts.Runs,counts.Deliveries,counts.Alerts,counts.Tokens,counts.Commands,Applied=builder.Configuration.GetValue<bool>("Privacy:Apply")}));return 0;}
 if(args.Contains("--migrate")){await db.Database.MigrateAsync();return 0;}
 if(args.Contains("--recover-user")){await Provisioning.RecoverUser(db,s.ServiceProvider.GetRequiredService<ISecretStore>(),builder.Configuration);return 0;}
 await Provisioning.CreateUser(db,s.ServiceProvider.GetRequiredService<ISecretStore>(),builder.Configuration);return 0;
}
// Startup NEVER changes production schema. Operator must run the explicit migration command.
using(var s=app.Services.CreateScope())
{
 var db=s.ServiceProvider.GetRequiredService<ManagementDb>();
 if((await db.Database.GetPendingMigrationsAsync()).Any())throw new InvalidOperationException("Pending database migrations; run --migrate first");
 _=s.ServiceProvider.GetRequiredService<ISecretStore>();
}
app.UseForwardedHeaders();
app.UseExceptionHandler(handler=>handler.Run(async c=>
{
 c.Response.StatusCode=500;c.Response.ContentType="application/problem+json";
 await c.Response.WriteAsJsonAsync(new{title="Request failed",status=500});
}));
app.Use(async(c,next)=>
{
 if(!app.Environment.IsDevelopment()&&!c.Request.IsHttps&&!c.Request.Path.StartsWithSegments("/health")){c.Response.StatusCode=400;return;}
 c.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
 c.Response.Headers["X-Content-Type-Options"]="nosniff";c.Response.Headers["Referrer-Policy"]="no-referrer";c.Response.Headers["Permissions-Policy"]="camera=(), microphone=(), geolocation=()";
 c.Response.Headers["X-Frame-Options"]="DENY";if(c.Request.IsHttps)c.Response.Headers["Strict-Transport-Security"]="max-age=31536000";
 if(!c.Request.Path.StartsWithSegments("/branding")&&!c.Request.Path.StartsWithSegments("/css")&&!c.Request.Path.StartsWithSegments("/js")&&!c.Request.Path.StartsWithSegments("/fonts"))c.Response.Headers.CacheControl="no-store";
 await next();
});
app.UseStaticFiles();app.UseRouting();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
app.Use(async(c,next)=>
{
 if(c.Request.Path.StartsWithSegments("/api/v1/management")&&c.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
 {
  if(c.User.Identity?.IsAuthenticated!=true){c.Response.StatusCode=401;return;}
  try{await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c);}catch(AntiforgeryValidationException){c.Response.StatusCode=400;return;}
 }
 await next();
});
app.MapHealthChecks("/health/ready");app.MapGet("/health/live",()=>Results.Ok(new{status="live"}));
app.MapHistoryApi();app.MapUpdateApi();app.MapNotificationApi();app.MapCommandApi();app.MapConfigurationApi();app.MapManagementApi();app.MapAgentApi();app.MapRazorPages();await app.RunAsync();return 0;
public partial class Program { }
