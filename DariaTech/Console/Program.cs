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
builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection("Monitoring"));
builder.Services.AddHostedService<Monitoring>();
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
if(args.Contains("--migrate")||args.Contains("--bootstrap-user"))
{
 using var s=app.Services.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Maintenance=true;
 var db=s.ServiceProvider.GetRequiredService<ManagementDb>();
 if(args.Contains("--migrate")){await db.Database.MigrateAsync();return 0;}
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
app.MapManagementApi();app.MapAgentApi();app.MapRazorPages();await app.RunAsync();return 0;
public partial class Program { }
