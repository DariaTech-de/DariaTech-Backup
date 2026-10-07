using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using DariaTech.Console.Services;
using DariaTech.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
namespace DariaTech.Tests;

[TestFixture,NonParallelizable]
public sealed class PostgresTests
{
 private string connection=null!;private string directory=null!;private TestFactory factory=null!;
 private ManagementDb Db(TenantScope? scope=null)=>new(new DbContextOptionsBuilder<ManagementDb>().UseNpgsql(connection).Options,scope??new TenantScope(new HttpContextAccessor()){Maintenance=true});
 [OneTimeSetUp]public async Task Setup()
 {
  connection=Environment.GetEnvironmentVariable("DARIATECH_TEST_DATABASE")??throw new InvalidOperationException("DARIATECH_TEST_DATABASE must point to a dedicated PostgreSQL test database");
  directory=Path.Combine(Path.GetTempPath(),"dariatech-tests-"+Guid.NewGuid());Directory.CreateDirectory(directory);
  File.WriteAllText(Path.Combine(directory,"master.key"),Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
  await using var db=Db();await db.Database.MigrateAsync();factory=new TestFactory(connection,directory);
 }
 [OneTimeTearDown]public void TearDown(){factory?.Dispose();if(directory is not null)Directory.Delete(directory,true);}
 private async Task<(Tenant Tenant,Site Site)> Customer()
 {
  await using var db=Db();var t=new Tenant{Name="Tenant "+Guid.NewGuid()};db.Tenants.Add(t);db.Customers.Add(new Customer{TenantId=t.Id,Name=t.Name,Number=Guid.NewGuid().ToString(),Notes="Internal MSP note"});var site=new Site{TenantId=t.Id,Name="HQ"};db.Sites.Add(site);await db.SaveChangesAsync();return(t,site);
 }
 private async Task<string> Token(Tenant t,Site s,bool expired=false)
 {
  var value=Tokens.Create();await using var db=Db();db.EnrollmentTokens.Add(new EnrollmentToken{TenantId=t.Id,SiteId=s.Id,TokenHash=Tokens.Hash(value),Expires=DateTimeOffset.UtcNow.AddMinutes(expired?-1:10)});await db.SaveChangesAsync();return value;
 }
 [Test]public async Task CompositeForeignKeysRejectCrossTenantLinks()
 {
  var(a,_)=await Customer();var(_,siteB)=await Customer();await using var db=Db();db.Devices.Add(new Device{TenantId=a.Id,SiteId=siteB.Id,Name="Must fail"});
  Assert.ThrowsAsync<DbUpdateException>(async()=>await db.SaveChangesAsync());
 }
 [Test]public async Task QueryFiltersIsolateTenantsAcrossCachedModelInstancesAndWrites()
 {
  var(a,_)=await Customer();var(b,_)=await Customer();
  var ctx=new DefaultHttpContext{User=new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant",a.Id.ToString()),new Claim(ClaimTypes.Role,"CustomerReadOnly")],"test"))};
  var scope=new TenantScope(new HttpContextAccessor{HttpContext=ctx});await using var db=Db(scope);
  Assert.That(await db.Customers.AnyAsync(x=>x.TenantId==a.Id),Is.True);Assert.That(await db.Customers.AnyAsync(x=>x.TenantId==b.Id),Is.False);
  db.Sites.Add(new Site{TenantId=b.Id,Name="Denied"});Assert.ThrowsAsync<UnauthorizedAccessException>(async()=>await db.SaveChangesAsync());
 }
 [Test]public async Task EnrollmentTokenIsAtomicSingleUseAndExpires()
 {
  var(t,s)=await Customer();var token=await Token(t,s);using var c=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  var request=new EnrollmentRequest(token,"DEVICE01","Windows","0.1.0");
  var responses=await Task.WhenAll(c.PostAsJsonAsync("/api/v1/agent/enroll",request),c.PostAsJsonAsync("/api/v1/agent/enroll",request));
  Assert.That(responses.Count(x=>x.StatusCode==HttpStatusCode.OK),Is.EqualTo(1));Assert.That(responses.Count(x=>x.StatusCode==HttpStatusCode.Unauthorized),Is.EqualTo(1));
  var expired=await Token(t,s,true);using var rejected=await c.PostAsJsonAsync("/api/v1/agent/enroll",request with{Token=expired});Assert.That(rejected.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
  await using var db=Db();var credential=await responses.Single(x=>x.IsSuccessStatusCode).Content.ReadFromJsonAsync<EnrollmentResponse>();
  var agent=await db.Agents.SingleAsync(x=>x.DeviceId==credential!.DeviceId);Assert.That(agent.CredentialHash,Is.Not.EqualTo(credential!.Credential));
 }
 [Test]public async Task DeviceCredentialsCannotImpersonateAnotherDeviceAndRunsAreIdempotent()
 {
  var(t,s)=await Customer();var token=await Token(t,s);using var c=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using var e=await c.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(token,"DEVICE02","Windows","0.1.0"));var identity=(await e.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  c.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);c.DefaultRequestHeaders.Add("X-Device-Id",Guid.NewGuid().ToString());
  var run=new RunReport("run-1",DateTimeOffset.UtcNow.AddMinutes(-10),DateTimeOffset.UtcNow.AddMinutes(-1),RunStatus.Success,1024,10,4096,null,null);var heartbeat=new HeartbeatRequest("0.1.0","Windows",true,[new("1","Daily",null,run)]);
  using var denied=await c.PostAsJsonAsync("/api/v1/agent/heartbeat",heartbeat);Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
  c.DefaultRequestHeaders.Remove("X-Device-Id");c.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var first=await c.PostAsJsonAsync("/api/v1/agent/heartbeat",heartbeat);Assert.That(first.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var replay=await c.PostAsJsonAsync("/api/v1/agent/heartbeat",heartbeat);Assert.That(replay.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  await using var db=Db();var job=await db.Jobs.SingleAsync(x=>x.DeviceId==identity.DeviceId);Assert.That(await db.Runs.CountAsync(x=>x.JobId==job.Id),Is.EqualTo(1));
  var agent=await db.Agents.SingleAsync(x=>x.DeviceId==identity.DeviceId);agent.Revoked=true;await db.SaveChangesAsync();using var revoked=await c.PostAsJsonAsync("/api/v1/agent/heartbeat",heartbeat);Assert.That(revoked.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
 }
 [Test]public async Task MonitoringDeduplicatesAndResolvesAlerts()
 {
  var(t,s)=await Customer();var d=new Device{TenantId=t.Id,SiteId=s.Id,Name="Offline",EngineReachable=true,LastHeartbeat=DateTimeOffset.UtcNow.AddHours(-1)};
  await using var db=Db();db.Devices.Add(d);db.Agents.Add(new DariaTech.Console.Data.Agent{TenantId=t.Id,DeviceId=d.Id,CredentialHash=Tokens.Hash(Tokens.Create())});var job=new BackupJob{TenantId=t.Id,DeviceId=d.Id,Name="Backup",LocalId="1"};db.Jobs.Add(job);await db.SaveChangesAsync();
  var now=DateTimeOffset.UtcNow;await Monitoring.Evaluate(db,new(),now);await Monitoring.Evaluate(db,new(),now.AddSeconds(1));
  Assert.That(await db.Alerts.CountAsync(x=>x.DeviceId==d.Id&&x.Key=="offline"),Is.EqualTo(1));
  d.LastHeartbeat=now;db.Runs.Add(new BackupRun{TenantId=t.Id,JobId=job.Id,LocalRunId="ok",Started=now.AddMinutes(-10),Completed=now,Status=RunStatus.Success});await db.SaveChangesAsync();await Monitoring.Evaluate(db,new(),now.AddSeconds(2));
  Assert.That(await db.Alerts.CountAsync(x=>x.DeviceId==d.Id&&x.Resolved==null),Is.EqualTo(0));
 }
 private async Task<HttpClient> Login(UserRole role,Guid? tenant=null,bool includeMfa=true,TestFactory? isolatedFactory=null)
 {
  var host=isolatedFactory??factory;using var scope=host.Services.CreateScope();var secrets=scope.ServiceProvider.GetRequiredService<ISecretStore>();await using var db=Db();
  const string password="test-only-strong-password-2810";var user=new User{Email=Guid.NewGuid()+"@test.invalid",Role=role,TenantId=tenant};var secret=Totp.CreateSecret();user.PasswordHash=new PasswordHasher<User>().HashPassword(user,password);user.TotpSecret=secrets.Protect(tenant??Guid.Empty,$"totp:{user.Id}",secret);db.Users.Add(user);await db.SaveChangesAsync();
  var c=host.CreateClient(new(){BaseAddress=new Uri("https://localhost"),AllowAutoRedirect=false});var form=await c.GetStringAsync("/Login");var match=Regex.Match(form,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(match.Success,Is.True);
  var values=new Dictionary<string,string>{{"Email",user.Email},{"Password",password},{"Code",includeMfa?Totp.Code(secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30):""},{"__RequestVerificationToken",WebUtility.HtmlDecode(match.Groups[1].Value)}};
  using var response=await c.PostAsync("/Login",new FormUrlEncodedContent(values));Assert.That(response.StatusCode,Is.EqualTo(includeMfa?HttpStatusCode.Redirect:HttpStatusCode.OK));return c;
 }
 [Test]public async Task RealLoginRequiresMfaAndManagementWritesRequireCsrfAndRbac()
 {
  using var anonymous=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost"),AllowAutoRedirect=false});using var denied=await anonymous.GetAsync("/api/v1/management/customers");Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
  using var withoutMfa=await Login(UserRole.SuperAdmin,includeMfa:false);using var noMfa=await withoutMfa.GetAsync("/api/v1/management/customers");Assert.That(noMfa.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
  using var admin=await Login(UserRole.SuperAdmin);var dashboard=await admin.GetStringAsync("/");Assert.That(dashboard,Does.Contain("Backup-Übersicht").And.Contain("DariaTech Backup"));using var missingCsrf=await admin.PostAsJsonAsync("/api/v1/management/customers",new{Name="No csrf"});Assert.That(missingCsrf.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest));
  using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  using var created=await admin.PostAsJsonAsync("/api/v1/management/customers",new{Name="Real customer",Number=Guid.NewGuid().ToString(),Contact="",Email="",Phone="",Address="",Notes="",Active=true});Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Created));
  using var readOnly=await Login(UserRole.ReadOnly);using var roCsrf=System.Text.Json.JsonDocument.Parse(await readOnly.GetStringAsync("/api/v1/management/csrf"));readOnly.DefaultRequestHeaders.Add("X-CSRF-Token",roCsrf.RootElement.GetProperty("token").GetString());using var forbidden=await readOnly.PostAsJsonAsync("/api/v1/management/customers",new{Name="Denied"});Assert.That(forbidden.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
 }
 [Test]public async Task CustomerPortalCannotReadForeignDevicesOrInternalNotes()
 {
  var(a,_)=await Customer();var(b,sb)=await Customer();await using var db=Db();var foreign=new Device{TenantId=b.Id,SiteId=sb.Id,Name="Foreign"};db.Devices.Add(foreign);await db.SaveChangesAsync();
  using var customer=await Login(UserRole.CustomerReadOnly,a.Id);var list=await customer.GetStringAsync("/api/v1/management/customers");Assert.That(list,Does.Not.Contain(b.Id.ToString()).And.Not.Contain("Internal MSP note"));
  using var denied=await customer.GetAsync($"/api/v1/management/devices/{foreign.Id}");Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
 }
 [Test]public async Task OperatorApiKeepsDeviceInTenantAndRevokesChangedUserSessions()
 {
  var(a,sa)=await Customer();var(b,sb)=await Customer();using var admin=await Login(UserRole.SuperAdmin);
  using var token=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",token.RootElement.GetProperty("token").GetString());
  await using var db=Db();var d=new Device{TenantId=a.Id,SiteId=sa.Id,Name="Original"};db.Devices.Add(d);await db.SaveChangesAsync();
  using var foreign=await admin.PutAsJsonAsync($"/api/v1/management/devices/{d.Id}",new{Name="Moved",SiteId=sb.Id});Assert.That(foreign.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
  using var own=await admin.PutAsJsonAsync($"/api/v1/management/devices/{d.Id}",new{Name="Renamed",SiteId=sa.Id});Assert.That(own.StatusCode,Is.EqualTo(HttpStatusCode.OK));
  var metadata=await admin.GetStringAsync("/api/v1/management/users");Assert.That(metadata,Does.Not.Contain("passwordHash").And.Not.Contain("totpSecret"));
  var actor=await db.Audit.Where(x=>x.Action=="login.success").OrderByDescending(x=>x.Id).Select(x=>x.Actor).FirstAsync();var userId=Guid.Parse(actor);
  // Retain a recovery admin so deactivating the caller does not exercise the last-admin conflict.
  using var scope=factory.Services.CreateScope();var secrets=scope.ServiceProvider.GetRequiredService<ISecretStore>();var recovery=new User{Email=Guid.NewGuid()+"@test.invalid",Role=UserRole.SuperAdmin};recovery.PasswordHash=UserPasswords.Create().HashPassword(recovery,"test-only-recovery-password-42");recovery.TotpSecret=secrets.Protect(Guid.Empty,$"totp:{recovery.Id}",Totp.CreateSecret());db.Users.Add(recovery);await db.SaveChangesAsync();
  using var changed=await admin.PutAsJsonAsync($"/api/v1/management/users/{userId}",new{Role=UserRole.SuperAdmin,TenantId=(Guid?)null,Active=false});Assert.That(changed.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var revoked=await admin.GetAsync("/api/v1/management/customers");Assert.That(revoked.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
 }
 [Test]public async Task CustomerFormAcceptsOptionalEmptyContactFields()
 {
  using var isolated=new TestFactory(connection,directory);using var admin=await Login(UserRole.SuperAdmin,isolatedFactory:isolated);
  var page=await admin.GetStringAsync("/Customers");var match=Regex.Match(page,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(match.Success,Is.True);
  var number=Guid.NewGuid().ToString();using var response=await admin.PostAsync("/Customers",new FormUrlEncodedContent(new Dictionary<string,string>{{"Input.Name","Form customer"},{"Input.Number",number},{"__RequestVerificationToken",WebUtility.HtmlDecode(match.Groups[1].Value)}}));
  Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));await using var db=Db();Assert.That(await db.Customers.AnyAsync(x=>x.Number==number),Is.True);
 }
 [Test]public async Task DatabaseRejectsAuditMutationEvenThroughRawSql()
 {
  await using var db=Db();var e=new AuditEvent{Actor="test",Action="test.append-only",Resource="fixture"};db.Audit.Add(e);await db.SaveChangesAsync();
  Assert.ThrowsAsync<Npgsql.PostgresException>(async()=>await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Audit" SET "Action"='changed' WHERE "Id"={e.Id}"""));
 }
 private sealed class TestFactory(string cs,string dir):WebApplicationFactory<Program>
 {
  protected override void ConfigureWebHost(IWebHostBuilder b)
  {
   b.UseEnvironment("Development");b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:Management",cs},{"Security:MasterKeyFile",Path.Combine(dir,"master.key")},{"Security:KeyDirectory",Path.Combine(dir,"keys")},{"Monitoring:PollSeconds","3600"}}));
  }
 }
}
