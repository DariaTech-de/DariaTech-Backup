using DariaTech.Console.Api;
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
  using(var signing=System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))File.WriteAllText(Path.Combine(directory,"commands.pem"),signing.ExportPkcs8PrivateKeyPem());
  await using var db=Db();await db.Database.MigrateAsync();factory=new TestFactory(connection,directory);
 }
 [SetUp]public void ResetHost(){factory?.Dispose();factory=new TestFactory(connection,directory);}
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
 [Test]public async Task DestinationPickerStoresEngineDestinationAndKeepsSecretsOnEdit()
 {
  var(t,site)=await Customer();await using var db=Db();var device=new Device{TenantId=t.Id,SiteId=site.Id,Name="Picker worker"};db.Devices.Add(device);await db.SaveChangesAsync();
  using var admin=await Login(UserRole.SuperAdmin);
  string Token(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  var form=await admin.GetStringAsync($"/BackupEdit?deviceId={device.Id}");
  Assert.That(form,Does.Contain("value=\"b2\"").And.Contain("value=\"smb\"").And.Contain("value=\"onedrivev2\""),"destination picker lists engine destinations");
  using var created=await admin.PostAsync("/BackupEdit",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"DeviceId",device.Id.ToString()},{"Name","B2 job"},{"Provider","Files"},{"Sources","C:\\Daten"},{"DestinationKey","b2"},
   {"dest.b2.host","kunde-backup"},{"dest.b2.path","geraet 1"},{"dest.b2.opt.b2-accountid","key-id-123"},{"dest.b2.opt.b2-applicationkey","b2-secret-value-456"},
   {"Passphrase","picker-passphrase-123456"},{"KeepVersions","30"},{"RepeatHours","24"},{"__RequestVerificationToken",Token(form)}}));
  Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Redirect),System.Text.RegularExpressions.Regex.Match(await created.Content.ReadAsStringAsync(),"form-error[^<]*<").Value);
  var managed=await db.ManagedJobs.SingleAsync(x=>x.DeviceId==device.Id);
  var secrets=factory.Services.GetRequiredService<ISecretStore>();
  ManagedBackupDefinition Latest(){var r=db.ConfigurationRevisions.AsNoTracking().Where(x=>x.ManagedJobId==managed.Id).OrderByDescending(x=>x.Revision).First();return DariaTech.Console.Api.ConfigurationApi.Read(secrets,r);}
  var stored=Latest();
  Assert.That(stored.TargetUrl,Is.EqualTo("b2://kunde-backup/geraet%201"));
  Assert.That(stored.BackendOptions["b2-applicationkey"],Is.EqualTo("b2-secret-value-456"));
  var device_page=WebUtility.HtmlDecode(await admin.GetStringAsync($"/Device?id={device.Id}"));Assert.That(device_page,Does.Contain("Ziel: Backblaze B2 · kunde-backup/geraet 1"));
  var edit=await admin.GetStringAsync($"/BackupEdit?deviceId={device.Id}&id={managed.Id}");
  Assert.That(edit,Does.Not.Contain("b2-secret-value-456").And.Not.Contain("picker-passphrase-123456").And.Contain("key-id-123"));
  using var changed=await admin.PostAsync("/BackupEdit",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"DeviceId",device.Id.ToString()},{"ManagedJobId",managed.Id.ToString()},{"Revision","1"},{"Name","B2 job renamed"},{"Provider","Files"},{"Sources","C:\\Daten"},
   {"DestinationKey","s3"},{"dest.b2.opt.b2-accountid","key-id-789"},{"KeepVersions","60"},{"RepeatHours","24"},{"__RequestVerificationToken",Token(edit)}}));
  Assert.That(changed.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  stored=Latest();
  Assert.That(stored.TargetUrl,Is.EqualTo("b2://kunde-backup/geraet%201"),"destination of an existing chain is fixed even if another key is posted");
  Assert.That(stored.BackendOptions["b2-applicationkey"],Is.EqualTo("b2-secret-value-456"),"blank secret keeps the stored value");
  Assert.That(stored.BackendOptions["b2-accountid"],Is.EqualTo("key-id-789"));Assert.That(stored.KeepVersions,Is.EqualTo(60));
  using var plain=await admin.PostAsync("/BackupEdit",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"DeviceId",device.Id.ToString()},{"Name","Plain FTP"},{"Provider","Files"},{"Sources","C:\\Daten"},{"DestinationKey","ftp"},
   {"dest.ftp.host","ftp.example"},{"dest.ftp.path","backup"},{"Passphrase","picker-passphrase-123456"},{"KeepVersions","30"},{"RepeatHours","24"},{"__RequestVerificationToken",Token(form)}}));
  Assert.That(plain.StatusCode,Is.EqualTo(HttpStatusCode.OK));Assert.That(await plain.Content.ReadAsStringAsync(),Does.Contain("ohne gesicherte Verbindung"));
 }
 [Test]public async Task SavedDestinationGivesEachJobItsOwnFolderAndStaysOperatorOnly()
 {
  var(t,site)=await Customer();await using var db=Db();var device=new Device{TenantId=t.Id,SiteId=site.Id,Name="Template worker"};db.Devices.Add(device);await db.SaveChangesAsync();
  var number=await db.Customers.Where(x=>x.TenantId==t.Id).Select(x=>x.Number).SingleAsync();
  using var admin=await Login(UserRole.SuperAdmin);
  string Token(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  var name="DariaTech-Speicher "+Guid.NewGuid().ToString("N")[..8];
  var page=await admin.GetStringAsync("/Targets");
  using var saved=await admin.PostAsync("/Targets?handler=Save",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"Name",name},{"IsDefault","true"},{"DestinationKey","ssh"},{"dest.ssh.host","storage.dariatech.example"},{"dest.ssh.port","2222"},{"dest.ssh.path","backups"},
   {"dest.ssh.opt.auth-username","backup"},{"dest.ssh.opt.auth-password","template-secret-321"},{"dest.ssh.opt.ssh-fingerprint","ssh-ed25519 256 11:22:33"},{"__RequestVerificationToken",Token(page)}}));
  Assert.That(saved.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  var template=await db.DestinationTemplates.AsNoTracking().SingleAsync(x=>x.Name==name);
  Assert.That(template.IsDefault,Is.True);Assert.That(template.EncryptedOptions,Does.Not.Contain("template-secret-321"));
  var list=await admin.GetStringAsync("/Targets");Assert.That(list,Does.Not.Contain("template-secret-321"));
  var form=await admin.GetStringAsync($"/BackupEdit?deviceId={device.Id}");Assert.That(form,Does.Contain(template.Id.ToString()));
  using var created=await admin.PostAsync("/BackupEdit",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"DeviceId",device.Id.ToString()},{"Name","Server täglich"},{"Provider","Files"},{"Sources","/srv/daten"},{"DestinationMode","template"},{"TemplateId",template.Id.ToString()},
   {"Passphrase","template-passphrase-123456"},{"KeepVersions","30"},{"RepeatHours","24"},{"__RequestVerificationToken",Token(form)}}));
  Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  var managed=await db.ManagedJobs.SingleAsync(x=>x.DeviceId==device.Id);
  var r=await db.ConfigurationRevisions.AsNoTracking().SingleAsync(x=>x.ManagedJobId==managed.Id);
  var stored=DariaTech.Console.Api.ConfigurationApi.Read(factory.Services.GetRequiredService<ISecretStore>(),r);
  Assert.That(stored.TargetUrl,Does.Match($"^ssh://storage\\.dariatech\\.example:2222/backups/{Regex.Escape(number)}/Template-worker/Server-t-glich-[0-9a-f]{{6}}$"));
  Assert.That(stored.BackendOptions["auth-password"],Is.EqualTo("template-secret-321"));Assert.That(stored.BackendOptions["ssh-fingerprint"],Is.EqualTo("ssh-ed25519 256 11:22:33"));
  using var customer=await Login(UserRole.CustomerAdmin,t.Id);using var denied=await customer.GetAsync("/Targets");Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
  db.DestinationTemplates.Remove(await db.DestinationTemplates.SingleAsync(x=>x.Id==template.Id));await db.SaveChangesAsync();
 }
 [Test]public async Task RealConsoleFormsCreateJobsAndQueueSignedActionsWithoutExposingSecrets()
 {
  var(t,site)=await Customer();await using var db=Db();var device=new Device{TenantId=t.Id,SiteId=site.Id,Name="Form-managed worker"};db.Devices.Add(device);await db.SaveChangesAsync();
  using var admin=await Login(UserRole.SuperAdmin);
  var form=await admin.GetStringAsync($"/BackupEdit?deviceId={device.Id}&provider=Microsoft365");
  var match=Regex.Match(form,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(match.Success,Is.True);
  var source=SaasTests.Office();var values=new List<KeyValuePair<string,string>>
  {
   new("DeviceId",device.Id.ToString()),new("Name","Cloud form job"),new("Provider","Microsoft365"),new("DirectoryTenant",source.DirectoryTenant),
   new("ClientId",source.Credentials["office365-client-id"]),new("ClientSecret",source.Credentials["office365-client-secret"]),
   new("TargetUrl","file:///D:/CloudStorage"),new("Passphrase","test-form-passphrase-987654"),new("KeepVersions","30"),
   new("RootTypes","Users"),new("UserTypes","Mailbox"),new("__RequestVerificationToken",WebUtility.HtmlDecode(match.Groups[1].Value))
  };
  using var created=await admin.PostAsync("/BackupEdit",new FormUrlEncodedContent(values));Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  var managed=await db.ManagedJobs.SingleAsync(x=>x.DeviceId==device.Id);
  var edit=await admin.GetStringAsync($"/BackupEdit?deviceId={device.Id}&id={managed.Id}");
  Assert.That(edit,Does.Not.Contain("test-form-passphrase-987654").And.Not.Contain(source.Credentials["office365-client-secret"]));
  var job=new BackupJob{TenantId=t.Id,DeviceId=device.Id,Name="Cloud form job",LocalId="1",Ownership="Managed"};db.Jobs.Add(job);await db.SaveChangesAsync();
  var actions=await admin.GetStringAsync($"/JobActions?id={job.Id}");
  match=Regex.Match(actions,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
  using var queued=await admin.PostAsync("/JobActions?handler=Request",new FormUrlEncodedContent(new Dictionary<string,string>{{"JobId",job.Id.ToString()},{"Action","RunBackup"},{"__RequestVerificationToken",WebUtility.HtmlDecode(match.Groups[1].Value)}}));
  Assert.That(queued.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  Assert.That(await db.Commands.AnyAsync(x=>x.JobId==job.Id&&x.Action==RemoteAction.RunBackup&&x.Status=="Pending"),Is.True);
  using var reader=await Login(UserRole.ReadOnly);using var blocked=await reader.GetAsync($"/BackupEdit?deviceId={device.Id}");Assert.That(blocked.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
 }

 [Test]public async Task DatabaseRejectsAuditMutationEvenThroughRawSql()
 {
  await using var db=Db();var e=new AuditEvent{Actor="test",Action="test.append-only",Resource="fixture"};db.Audit.Add(e);await db.SaveChangesAsync();
  Assert.ThrowsAsync<Npgsql.PostgresException>(async()=>await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Audit" SET "Action"='changed' WHERE "Id"={e.Id}"""));
 }

 [Test]public async Task ManagedConfigurationIsEncryptedDeviceBoundVersionedAndAudited()
 {
  var(t,site)=await Customer();using var agent=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using var enrolled=await agent.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"Managed","Windows","0.1.0"));
  var identity=(await enrolled.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  agent.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);agent.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var admin=await Login(UserRole.SuperAdmin);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  var definition=new ManagedBackupDefinition("Central backup",["C:\\Data"],"file:///D:/Backups/","test-managed-passphrase-987654",new(),30,[],null);
  using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{identity.DeviceId}/managed-jobs",new ConfigurationInput(0,definition));
  Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Created));var job=(await created.Content.ReadFromJsonAsync<ManagedJob>())!;
  await using var db=Db();var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==job.Id);
  Assert.That(revision.EncryptedConfiguration,Does.Not.Contain(definition.Passphrase).And.Not.Contain("C:\\Data"));
  var management=await admin.GetStringAsync("/api/v1/management/managed-jobs");Assert.That(management,Does.Not.Contain(definition.Passphrase).And.Not.Contain("EncryptedConfiguration"));
  var assignments=await agent.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations");Assert.That(assignments!.Single().Definition.Passphrase,Is.EqualTo(definition.Passphrase));
  using var ownReceipt=await agent.PostAsJsonAsync($"/api/v1/agent/configurations/{job.Id}/receipt",new ConfigurationReceipt(1,"7","Applied"));Assert.That(ownReceipt.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var replay=await agent.PostAsJsonAsync($"/api/v1/agent/configurations/{job.Id}/receipt",new ConfigurationReceipt(1,"7","Applied"));Assert.That(replay.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  Assert.That(await db.Audit.CountAsync(x=>x.Resource==job.Id.ToString()&&x.Action=="backup.configuration-applied"),Is.EqualTo(1));
  Assert.That((await db.Jobs.SingleAsync(x=>x.DeviceId==identity.DeviceId)).Ownership,Is.EqualTo("Managed"));
  using var mutation=await admin.PutAsJsonAsync($"/api/v1/management/managed-jobs/{job.Id}",new ConfigurationInput(1,definition with{Passphrase="different-secret-password"}));Assert.That(mutation.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var changed=await admin.PutAsJsonAsync($"/api/v1/management/managed-jobs/{job.Id}",new ConfigurationInput(1,definition with{KeepVersions=90}));Assert.That(changed.StatusCode,Is.EqualTo(HttpStatusCode.OK));
  using var conflict=await admin.PutAsJsonAsync($"/api/v1/management/managed-jobs/{job.Id}",new ConfigurationInput(1,definition));Assert.That(conflict.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  var(other,otherSite)=await Customer();using var foreign=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using var otherEnroll=await foreign.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(other,otherSite),"Foreign","Windows","0.1.0"));var otherIdentity=(await otherEnroll.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  foreign.DefaultRequestHeaders.Authorization=new("Bearer",otherIdentity.Credential);foreign.DefaultRequestHeaders.Add("X-Device-Id",otherIdentity.DeviceId.ToString());
  Assert.That(await foreign.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"),Is.Empty);
  using var forbidden=await foreign.PostAsJsonAsync($"/api/v1/agent/configurations/{job.Id}/receipt",new ConfigurationReceipt(2,"7","Applied"));Assert.That(forbidden.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
  revision.EncryptedConfiguration="tampered";Assert.ThrowsAsync<InvalidOperationException>(async()=>await db.SaveChangesAsync());
 }

 [Test]public async Task CloudSecretsAreEncryptedAndRestoreCannotSkipRevisionOrSecondApproval()
 {
  var(t,site)=await Customer();using var agent=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using var enrolled=await agent.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"CloudWorker","Windows","0.2.0"));
  var identity=(await enrolled.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  agent.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);agent.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var admin=await Login(UserRole.SuperAdmin);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  var source=SaasTests.Office();var definition=new ManagedBackupDefinition("Cloud",[],"file:///D:/CloudStorage","test-cloud-passphrase",new(),30,[],null,source);
  using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{identity.DeviceId}/managed-jobs",new ConfigurationInput(0,definition));
  Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Created));var managed=(await created.Content.ReadFromJsonAsync<ManagedJob>())!;
  await using var db=Db();var revision=await db.ConfigurationRevisions.SingleAsync(x=>x.ManagedJobId==managed.Id);
  Assert.That(revision.EncryptedConfiguration,Does.Not.Contain(source.Credentials["office365-client-secret"]));
  Assert.That(await admin.GetStringAsync("/api/v1/management/managed-jobs"),Does.Not.Contain(source.Credentials["office365-client-secret"]));
  using var received=await agent.PostAsJsonAsync($"/api/v1/agent/configurations/{managed.Id}/receipt",new ConfigurationReceipt(1,"21","Applied"));Assert.That(received.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var job=await db.Jobs.SingleAsync(x=>x.DeviceId==identity.DeviceId);
  var selection=new SaasRestoreSelection(1,DateTimeOffset.UtcNow.AddDays(-1),["/virtual/users/user/mail"],"users/user/mailbox",true);
  using var wrong=await admin.PostAsJsonAsync("/api/v1/management/commands",new CommandInput(job.Id,RemoteAction.RestoreSaas,null,15,SaasRestore:selection with{ConfigurationRevision=2}));Assert.That(wrong.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var queued=await admin.PostAsJsonAsync("/api/v1/management/commands",new CommandInput(job.Id,RemoteAction.RestoreSaas,null,15,SaasRestore:selection));Assert.That(queued.StatusCode,Is.EqualTo(HttpStatusCode.Created));
  using var result=System.Text.Json.JsonDocument.Parse(await queued.Content.ReadAsStringAsync());var commandId=result.RootElement.GetProperty("id").GetGuid();
  Assert.That(await agent.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands"),Is.Empty);
  using var self=await admin.PostAsync($"/api/v1/management/commands/{commandId}/approve",null);Assert.That(self.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var foreignMutation=await admin.PutAsJsonAsync($"/api/v1/management/managed-jobs/{managed.Id}",new ConfigurationInput(1,definition with{Saas=source with{DirectoryTenant="44444444-4444-4444-4444-444444444444"}}));Assert.That(foreignMutation.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
 }

 [Test]public async Task GuestImageRestoreIsRevisionBoundSignedAndRequiresAnotherAdministrator()
 {
  var(t,site)=await Customer();using var agent=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using var enrolled=await agent.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"Proxmox","Linux","0.2.0","linux-x64"));
  var identity=(await enrolled.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  agent.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);agent.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var admin=await Login(UserRole.SuperAdmin);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  var definition=new ManagedBackupDefinition("Proxmox guests",[],"file:///backup/","test-guest-passphrase",new(),30,[],null,Proxmox:new([101,102]));
  using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{identity.DeviceId}/managed-jobs",new ConfigurationInput(0,definition));
  Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Created));var managed=(await created.Content.ReadFromJsonAsync<ManagedJob>())!;
  using var received=await agent.PostAsJsonAsync($"/api/v1/agent/configurations/{managed.Id}/receipt",new ConfigurationReceipt(1,"31","Applied"));Assert.That(received.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  await using var db=Db();var job=await db.Jobs.SingleAsync(x=>x.DeviceId==identity.DeviceId);
  var selection=new ProxmoxRestoreSelection(1,DateTimeOffset.UtcNow.AddDays(-1),"/staging/latest/vzdump-qemu-101-2026_10_07-08_00_00.vma.zst",901,"customer-storage",true);
  using var wrong=await admin.PostAsJsonAsync("/api/v1/management/commands",new CommandInput(job.Id,RemoteAction.RestoreProxmox,null,15,ProxmoxRestore:selection with{ConfigurationRevision=2}));Assert.That(wrong.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var unconfirmed=await admin.PostAsJsonAsync("/api/v1/management/commands",new CommandInput(job.Id,RemoteAction.RestoreProxmox,null,15,ProxmoxRestore:selection with{ConfirmImport=false}));Assert.That(unconfirmed.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest));
  using var queued=await admin.PostAsJsonAsync("/api/v1/management/commands",new CommandInput(job.Id,RemoteAction.RestoreProxmox,null,15,ProxmoxRestore:selection));Assert.That(queued.StatusCode,Is.EqualTo(HttpStatusCode.Created));
  using var response=System.Text.Json.JsonDocument.Parse(await queued.Content.ReadAsStringAsync());var id=response.RootElement.GetProperty("id").GetGuid();
  Assert.That(await agent.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands"),Is.Empty);
  using var self=await admin.PostAsync($"/api/v1/management/commands/{id}/approve",null);Assert.That(self.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var second=await Login(UserRole.Administrator);using var secondCsrf=System.Text.Json.JsonDocument.Parse(await second.GetStringAsync("/api/v1/management/csrf"));second.DefaultRequestHeaders.Add("X-CSRF-Token",secondCsrf.RootElement.GetProperty("token").GetString());
  using var approved=await second.PostAsync($"/api/v1/management/commands/{id}/approve",null);Assert.That(approved.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var envelopes=await agent.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands");using var key=System.Security.Cryptography.ECDsa.Create();key.ImportFromPem(File.ReadAllText(Path.Combine(directory,"commands.pem")));
  var command=CommandProtocol.Verify(envelopes!.Single(),key,identity.DeviceId,DateTimeOffset.UtcNow);
  Assert.That(command.Action,Is.EqualTo(RemoteAction.RestoreProxmox));Assert.That(command.ProxmoxRestore,Is.EqualTo(selection));
  Assert.That(await db.Audit.CountAsync(x=>x.Resource==id.ToString()),Is.EqualTo(2));
 }

 [Test]public async Task FolderBrowseAndDestinationTestAreSignedDeviceQueries()
 {
  var(t,site)=await Customer();using var agent=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using var enrolled=await agent.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"Query","Windows","0.1.0"));var identity=(await enrolled.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  agent.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);agent.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var admin=await Login(UserRole.SuperAdmin);
  var form=await admin.GetStringAsync($"/BackupEdit?deviceId={identity.DeviceId}");var token=WebUtility.HtmlDecode(Regex.Match(form,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
  async Task<Guid> Start(string handler,Dictionary<string,string> values)
  {
   values["__RequestVerificationToken"]=token;using var r=await admin.PostAsync($"/BackupEdit?handler={handler}",new FormUrlEncodedContent(values));Assert.That(r.StatusCode,Is.EqualTo(HttpStatusCode.OK));
   using var d=System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.That(d.RootElement.TryGetProperty("id",out var id),Is.True,d.RootElement.ToString());return id.GetGuid();
  }
  async Task<System.Text.Json.JsonElement> Query(Guid id)=>System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync($"/BackupEdit?handler=Query&deviceId={identity.DeviceId}&id={id}")).RootElement.Clone();
  using var key=System.Security.Cryptography.ECDsa.Create();key.ImportFromPem(File.ReadAllText(Path.Combine(directory,"commands.pem")));
  var browse=await Start("Browse",new(){{"deviceId",identity.DeviceId.ToString()},{"path","D:\\"}});
  Assert.That((await Query(browse)).GetProperty("done").GetBoolean(),Is.False);
  var command=CommandProtocol.Verify((await agent.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands"))!.Single(),key,identity.DeviceId,DateTimeOffset.UtcNow);
  Assert.That(command.Action,Is.EqualTo(RemoteAction.BrowseFolders));Assert.That(command.LocalJobId,Is.EqualTo("0"));Assert.That(command.Catalog!.Prefix,Is.EqualTo("D:\\"));
  using var withFile=await agent.PostAsJsonAsync($"/api/v1/agent/commands/{browse}/receipt",new CommandReceipt("Completed",null,null,new RestoreCatalog([],[new("D:\\secret.txt",5,false)],false)));Assert.That(withFile.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest),"browse returns folders only");
  using var folders=await agent.PostAsJsonAsync($"/api/v1/agent/commands/{browse}/receipt",new CommandReceipt("Completed",null,null,new RestoreCatalog([],[new("D:\\Daten\\",null,true),new("D:\\Fotos\\",null,true)],false)));Assert.That(folders.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var listed=await Query(browse);Assert.That(listed.GetProperty("ok").GetBoolean(),Is.True);Assert.That(listed.GetProperty("folders").EnumerateArray().Select(x=>x.GetString()),Is.EqualTo(new[]{"D:\\Daten\\","D:\\Fotos\\"}));
  var test=await Start("TestDestination",new(){{"DeviceId",identity.DeviceId.ToString()},{"Name","x"},{"Provider","Files"},{"DestinationKey","ssh"},{"dest.ssh.host","sftp.example"},{"dest.ssh.path","backup"},
   {"dest.ssh.opt.auth-username","u"},{"dest.ssh.opt.auth-password","test-secret-555"},{"dest.ssh.opt.ssh-fingerprint","ssh-ed25519 256 00:11"},{"createFolder","false"}});
  var testCommand=CommandProtocol.Verify((await agent.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands"))!.Single(),key,identity.DeviceId,DateTimeOffset.UtcNow);
  Assert.That(testCommand.Test!.TargetUrl,Is.EqualTo("ssh://sftp.example/backup"));Assert.That(testCommand.Test.Options["auth-password"],Is.EqualTo("test-secret-555"));
  await using(var store=Db())Assert.That((await store.Commands.SingleAsync(x=>x.Id==test)).EncryptedPayload,Does.Not.Contain("test-secret-555"));
  using var wrongDetail=await agent.PostAsJsonAsync($"/api/v1/agent/commands/{test}/receipt",new CommandReceipt("Failed",null,"DestinationTestFailed",null,"raw engine text"));Assert.That(wrongDetail.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest),"only fingerprints may be reported");
  using var mismatch=await agent.PostAsJsonAsync($"/api/v1/agent/commands/{test}/receipt",new CommandReceipt("Failed",null,"DestinationHostKeyMismatch",null,"ssh-ed25519 256 aa:bb:cc"));Assert.That(mismatch.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var tested=await Query(test);Assert.That(tested.GetProperty("ok").GetBoolean(),Is.False);Assert.That(tested.GetProperty("code").GetString(),Is.EqualTo("DestinationHostKeyMismatch"));Assert.That(tested.GetProperty("detail").GetString(),Is.EqualTo("ssh-ed25519 256 aa:bb:cc"));
  var plain=new FormUrlEncodedContent(new Dictionary<string,string>{{"DeviceId",identity.DeviceId.ToString()},{"Provider","Files"},{"DestinationKey","ftp"},{"dest.ftp.host","ftp.example"},{"dest.ftp.path","b"},{"__RequestVerificationToken",token}});
  using var rejected=await admin.PostAsync("/BackupEdit?handler=TestDestination",plain);Assert.That(await rejected.Content.ReadAsStringAsync(),Does.Contain("error"),"plain FTP is not even sent to the device");
  using var reader=await Login(UserRole.ReadOnly);using var denied=await reader.GetAsync($"/BackupEdit?handler=Query&deviceId={identity.DeviceId}&id={browse}");Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
 }
 [Test]public async Task RestoreRequiresIndependentApprovalAndCommandsAreDeviceBound()
 {
  var(t,site)=await Customer();using var agent=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using var enrolled=await agent.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"Remote","Windows","0.1.0"));var identity=(await enrolled.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  agent.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);agent.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  await using var db=Db();var job=new BackupJob{TenantId=t.Id,DeviceId=identity.DeviceId,LocalId="1",Name="Remote"};db.Jobs.Add(job);await db.SaveChangesAsync();
  using var admin=await Login(UserRole.SuperAdmin);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  using var created=await admin.PostAsJsonAsync("/api/v1/management/commands",new CommandInput(job.Id,RemoteAction.Restore,new(DateTimeOffset.UtcNow.AddDays(-1),["C:\\Data\\file"],"recovery-1"),15));Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Created));
  using var doc=System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync());var id=doc.RootElement.GetProperty("id").GetGuid();
  Assert.That(await agent.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands"),Is.Empty);
  using var self=await admin.PostAsync($"/api/v1/management/commands/{id}/approve",null);Assert.That(self.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var second=await Login(UserRole.Administrator);using var secondCsrf=System.Text.Json.JsonDocument.Parse(await second.GetStringAsync("/api/v1/management/csrf"));second.DefaultRequestHeaders.Add("X-CSRF-Token",secondCsrf.RootElement.GetProperty("token").GetString());using var approved=await second.PostAsync($"/api/v1/management/commands/{id}/approve",null);Assert.That(approved.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var envelopes=await agent.GetFromJsonAsync<SignedCommand[]>("/api/v1/agent/commands");using var key=System.Security.Cryptography.ECDsa.Create();key.ImportFromPem(File.ReadAllText(Path.Combine(directory,"commands.pem")));var command=CommandProtocol.Verify(envelopes!.Single(),key,identity.DeviceId,DateTimeOffset.UtcNow);Assert.That(command.Id,Is.EqualTo(id));
  using var receipt=await agent.PostAsJsonAsync($"/api/v1/agent/commands/{id}/receipt",new CommandReceipt("Accepted",19,null));Assert.That(receipt.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var complete=await agent.PostAsJsonAsync($"/api/v1/agent/commands/{id}/receipt",new CommandReceipt("Completed",19,null));Assert.That(complete.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var replay=await agent.PostAsJsonAsync($"/api/v1/agent/commands/{id}/receipt",new CommandReceipt("Accepted",19,null));Assert.That(replay.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  Assert.That(await db.Audit.CountAsync(x=>x.Resource==id.ToString()),Is.EqualTo(4));
  using var catalogRequest=await admin.PostAsJsonAsync("/api/v1/management/commands",new CommandInput(job.Id,RemoteAction.ListRestoreFiles,null,15,new(DateTimeOffset.UtcNow.AddDays(-1),null)));using var catalogDoc=System.Text.Json.JsonDocument.Parse(await catalogRequest.Content.ReadAsStringAsync());var catalogId=catalogDoc.RootElement.GetProperty("id").GetGuid();
  var catalog=new RestoreCatalog([],[new("C:\\Private\\patient.txt",10,false)],false);
  using var catalogReceipt=await agent.PostAsJsonAsync($"/api/v1/agent/commands/{catalogId}/receipt",new CommandReceipt("Completed",null,null,catalog));Assert.That(catalogReceipt.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var encrypted=await db.Commands.Where(x=>x.Id==catalogId).Select(x=>x.EncryptedCatalog).SingleAsync();Assert.That(encrypted,Does.Not.Contain("patient.txt"));
  var viewed=await admin.GetStringAsync($"/api/v1/management/commands/{catalogId}/catalog");Assert.That(viewed,Does.Contain("patient.txt"));
  using var readonlyUser=await Login(UserRole.ReadOnly);using var catalogDenied=await readonlyUser.GetAsync($"/api/v1/management/commands/{catalogId}/catalog");Assert.That(catalogDenied.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
 }

 [Test]public async Task NotificationOutboxDeduplicatesRetriesRemindsAndSendsRecovery()
 {
  var(t,site)=await Customer();await using var db=Db();var device=new Device{TenantId=t.Id,SiteId=site.Id,Name="Offline device"};db.Devices.Add(device);
  var now=DateTimeOffset.UtcNow;var alert=new Alert{TenantId=t.Id,DeviceId=device.Id,Key="offline",Code="DeviceOffline",Opened=now,LastSeen=now};db.Alerts.Add(alert);
  var rule=new NotificationRule{TenantId=t.Id,Recipient="alerts@test.invalid",Enabled=true,RepeatMinutes=60};db.NotificationRules.Add(rule);await db.SaveChangesAsync();await db.Entry(alert).ReloadAsync();
  await Notifications.Plan(db,now);await Notifications.Plan(db,now.AddSeconds(1));Assert.That(await db.Deliveries.CountAsync(x=>x.RuleId==rule.Id),Is.EqualTo(1));
  var delivery=await db.Deliveries.SingleAsync(x=>x.RuleId==rule.Id);var transport=new TestNotificationTransport{Fail=true};
  // The worker uses global maintenance scope; disable unrelated fixture rules when asserting order.
  await db.NotificationRules.Where(x=>x.Id!=rule.Id).ExecuteUpdateAsync(q=>q.SetProperty(x=>x.Enabled,false));await Notifications.Plan(db,now);
  Assert.That(await Notifications.DeliverOne(db,transport,now),Is.True);await db.Entry(delivery).ReloadAsync();Assert.That(delivery.Status,Is.EqualTo("Retry"));Assert.That(delivery.ErrorCode,Is.EqualTo("DeliveryFailed"));
  Assert.That(await Notifications.DeliverOne(db,transport,now.AddMinutes(1)),Is.False);
  transport.Fail=false;await Notifications.DeliverOne(db,transport,now.AddMinutes(6));await db.Entry(delivery).ReloadAsync();Assert.That(delivery.Status,Is.EqualTo("Sent"));Assert.That(transport.Messages,Has.Count.EqualTo(1));
  await Notifications.Plan(db,now.AddMinutes(30));Assert.That(await db.Deliveries.CountAsync(x=>x.RuleId==rule.Id),Is.EqualTo(1));
  await Notifications.Plan(db,now.AddHours(2));Assert.That(await db.Deliveries.CountAsync(x=>x.RuleId==rule.Id&&x.Kind=="Alert"),Is.EqualTo(2));
  alert.Resolved=now.AddHours(2);await db.SaveChangesAsync();await Notifications.Plan(db,now.AddHours(2));Assert.That(await db.Deliveries.CountAsync(x=>x.RuleId==rule.Id&&x.Kind=="Recovery"),Is.EqualTo(1));
  await Notifications.DeliverOne(db,transport,now.AddHours(2));Assert.That(transport.Messages.Last().Subject,Does.Contain("Entwarnung"));
  Assert.That(transport.Messages.All(x=>!x.Body.Contains("password",StringComparison.OrdinalIgnoreCase)),Is.True);
  var(_,foreignSite)=await Customer();var illegal=new NotificationDelivery{TenantId=t.Id,AlertId=alert.Id,RuleId=Guid.NewGuid()};db.Deliveries.Add(illegal);Assert.ThrowsAsync<DbUpdateException>(async()=>await db.SaveChangesAsync());
 }
 private sealed class TestNotificationTransport:INotificationTransport
 {
  public bool Fail {get;set;} public List<NotificationMessage> Messages {get;}=[];
  public Task Send(NotificationMessage message,CancellationToken ct){if(Fail)throw new InvalidOperationException("test-only simulated delivery failure");Messages.Add(message);return Task.CompletedTask;}
 }

 [Test]public async Task SignedUpdateApprovalIsAdminOnlyAndDeploymentIsDeviceBound()
 {
  var(t,site)=await Customer();using var agent=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});using var enrolled=await agent.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"Updater","Windows","0.1.0"));var identity=(await enrolled.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  agent.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);agent.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var admin=await Login(UserRole.SuperAdmin);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  using var key=System.Security.Cryptography.ECDsa.Create();key.ImportFromPem(File.ReadAllText(Path.Combine(directory,"commands.pem")));var now=DateTimeOffset.UtcNow;
  var m=new AgentUpdateManifest(Guid.NewGuid(),100,"DariaTechBackupAgent","win-x64","0.3.0.0","https://github.com/DariaTech-de/DariaTech-Backup/releases/download/test/Setup.exe",new string('A',64),2048,now,now.AddDays(7));var signed=UpdateProtocol.Sign(m,key);
  using var approved=await admin.PostAsJsonAsync("/api/v1/management/agent-releases",signed);Assert.That(approved.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var replay=await admin.PostAsJsonAsync("/api/v1/management/agent-releases",signed);Assert.That(replay.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var deployment=await admin.PostAsJsonAsync("/api/v1/management/update-deployments",new{DeviceId=identity.DeviceId,ReleaseId=m.ReleaseId});Assert.That(deployment.StatusCode,Is.EqualTo(HttpStatusCode.OK));
  var assignment=await agent.GetFromJsonAsync<UpdateAssignment>("/api/v1/agent/update");Assert.That(UpdateProtocol.Verify(assignment!.Manifest,key,now).ReleaseId,Is.EqualTo(m.ReleaseId));
  using var downloaded=await agent.PostAsJsonAsync($"/api/v1/agent/updates/{assignment.DeploymentId}/receipt",new UpdateReceipt("Downloaded",null));Assert.That(downloaded.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var applying=await agent.PostAsJsonAsync($"/api/v1/agent/updates/{assignment.DeploymentId}/receipt",new UpdateReceipt("Applying",null));Assert.That(applying.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var installed=await agent.PostAsJsonAsync($"/api/v1/agent/updates/{assignment.DeploymentId}/receipt",new UpdateReceipt("Installed",null));Assert.That(installed.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var rollback=await agent.PostAsJsonAsync($"/api/v1/agent/updates/{assignment.DeploymentId}/receipt",new UpdateReceipt("Applying",null));Assert.That(rollback.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  var linux=m with{ReleaseId=Guid.NewGuid(),Platform="linux-x64",ArtifactUrl="https://github.com/DariaTech-de/DariaTech-Backup/releases/download/test/agent.tar.gz"};
  using var linuxApproved=await admin.PostAsJsonAsync("/api/v1/management/agent-releases",UpdateProtocol.Sign(linux,key));Assert.That(linuxApproved.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var wrongPackage=await admin.PostAsJsonAsync("/api/v1/management/update-deployments",new DeploymentInput(identity.DeviceId,linux.ReleaseId));Assert.That(wrongPackage.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var linuxAgent=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using var linuxEnrollment=await linuxAgent.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"LinuxUpdater","Linux","0.2.0","linux-x64"));var linuxIdentity=(await linuxEnrollment.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  linuxAgent.DefaultRequestHeaders.Authorization=new("Bearer",linuxIdentity.Credential);linuxAgent.DefaultRequestHeaders.Add("X-Device-Id",linuxIdentity.DeviceId.ToString());
  using var wrongPlatform=await admin.PostAsJsonAsync("/api/v1/management/update-deployments",new DeploymentInput(linuxIdentity.DeviceId,m.ReleaseId));Assert.That(wrongPlatform.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  using var linuxDeploy=await admin.PostAsJsonAsync("/api/v1/management/update-deployments",new DeploymentInput(linuxIdentity.DeviceId,linux.ReleaseId));Assert.That(linuxDeploy.StatusCode,Is.EqualTo(HttpStatusCode.OK));
  var linuxAssignment=await linuxAgent.GetFromJsonAsync<UpdateAssignment>("/api/v1/agent/update");Assert.That(UpdateProtocol.Verify(linuxAssignment!.Manifest,key,now).Platform,Is.EqualTo("linux-x64"));
  using var platformMutation=await linuxAgent.PostAsJsonAsync("/api/v1/agent/heartbeat",new HeartbeatRequest("0.2.0","Linux",true,[],Platform:"osx-x64"));Assert.That(platformMutation.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest));

  var(otherTenant,otherSite)=await Customer();using var foreign=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});using var otherEnroll=await foreign.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(otherTenant,otherSite),"OtherUpdater","Windows","0.1.0"));var other=(await otherEnroll.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  foreign.DefaultRequestHeaders.Authorization=new("Bearer",other.Credential);foreign.DefaultRequestHeaders.Add("X-Device-Id",other.DeviceId.ToString());using var none=await foreign.GetAsync("/api/v1/agent/update");Assert.That(none.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var denied=await foreign.PostAsJsonAsync($"/api/v1/agent/updates/{assignment.DeploymentId}/receipt",new UpdateReceipt("Installed",null));Assert.That(denied.StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
 }

 [Test]public async Task LocalRecoveryRotatesCredentialsAndRevokesExistingSessions()
 {
  using var userClient=await Login(UserRole.SuperAdmin);await using var db=Db();var actor=await db.Audit.Where(x=>x.Action=="login.success").OrderByDescending(x=>x.Id).Select(x=>x.Actor).FirstAsync();var user=await db.Users.SingleAsync(x=>x.Id==Guid.Parse(actor));var oldVersion=user.SessionVersion;var oldSecret=user.TotpSecret;
  var passwordFile=Path.Combine(directory,"recovery-password-"+Guid.NewGuid());var output=passwordFile+".totp";File.WriteAllText(passwordFile,"test-only-new-recovery-password-8291");
  using var serviceScope=factory.Services.CreateScope();var secrets=serviceScope.ServiceProvider.GetRequiredService<ISecretStore>();var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Provision:Email",user.Email},{"Provision:PasswordFile",passwordFile},{"Provision:TotpOutputFile",output}}).Build();
  await Provisioning.RecoverUser(db,secrets,config);Assert.That(user.SessionVersion,Is.Not.EqualTo(oldVersion));Assert.That(user.TotpSecret,Is.Not.EqualTo(oldSecret));Assert.That(File.Exists(output),Is.True);Assert.That(await db.Audit.AnyAsync(x=>x.Action=="user.credentials-recovered"&&x.Resource==user.Id.ToString()),Is.True);
  using var revoked=await userClient.GetAsync("/api/v1/management/customers");Assert.That(revoked.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
  var originalVersion=user.SessionVersion;Assert.ThrowsAsync<IOException>(async()=>await Provisioning.RecoverUser(db,secrets,config));
  await db.Entry(user).ReloadAsync();Assert.That(user.SessionVersion,Is.EqualTo(originalVersion));
  File.Delete(passwordFile);File.Delete(output);
 }

 [Test]public async Task HistoricalRunsAreDeviceBoundAndIdempotent()
 {
  var(t,site)=await Customer();using var agent=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});using var enrolled=await agent.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"History","Windows","0.1.0"));var identity=(await enrolled.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
  agent.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);agent.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());
  using var heartbeat=await agent.PostAsJsonAsync("/api/v1/agent/heartbeat",new HeartbeatRequest("0.1.0","Windows",true,[new("1","History",null,null)]));Assert.That(heartbeat.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var now=DateTimeOffset.UtcNow;var request=new HistoryRequest([new("1",1,new("historical-1",now.AddDays(-2),now.AddDays(-2).AddMinutes(5),RunStatus.Success,100,1,null,null,null)),new("1",2,new("historical-2",now.AddDays(-1),now.AddDays(-1).AddMinutes(5),RunStatus.Failed,100,1,null,null,"BackupFailed"))]);
  using var saved=await agent.PostAsJsonAsync("/api/v1/agent/history",request);Assert.That(saved.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));using var replay=await agent.PostAsJsonAsync("/api/v1/agent/history",request);Assert.That(replay.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  await using var db=Db();var job=await db.Jobs.SingleAsync(x=>x.DeviceId==identity.DeviceId);Assert.That(await db.Runs.CountAsync(x=>x.JobId==job.Id),Is.EqualTo(2));
  using var unknown=await agent.PostAsJsonAsync("/api/v1/agent/history",new HistoryRequest([request.Runs[0] with{LocalJobId="foreign"}]));Assert.That(unknown.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
 }

 [Test]public async Task RetentionRemovesOldCatalogsButKeepsActiveCommandsAndAudit()
 {
  var(t,site)=await Customer();await using var db=Db();var d=new Device{TenantId=t.Id,SiteId=site.Id};db.Devices.Add(d);var job=new BackupJob{TenantId=t.Id,DeviceId=d.Id,LocalId="retention"};db.Jobs.Add(job);
  var old=DateTimeOffset.UtcNow.AddDays(-200);var removed=new RemoteCommand{TenantId=t.Id,DeviceId=d.Id,JobId=job.Id,Expires=old,Status="Completed",EncryptedCatalog="test-encrypted-paths"};
  var active=new RemoteCommand{TenantId=t.Id,DeviceId=d.Id,JobId=job.Id,Expires=old,Status="Accepted"};var recent=new RemoteCommand{TenantId=t.Id,DeviceId=d.Id,JobId=job.Id,Expires=DateTimeOffset.UtcNow,Status="Completed"};db.Commands.AddRange(removed,active,recent);await db.SaveChangesAsync();
  var preview=await Privacy.Prune(db,180,false);Assert.That(preview.Commands,Is.GreaterThanOrEqualTo(1));Assert.That(await db.Commands.AnyAsync(x=>x.Id==removed.Id),Is.True);
  await Privacy.Prune(db,180,true);Assert.That(await db.Commands.AnyAsync(x=>x.Id==removed.Id),Is.False);Assert.That(await db.Commands.AnyAsync(x=>x.Id==active.Id),Is.True);Assert.That(await db.Commands.AnyAsync(x=>x.Id==recent.Id),Is.True);Assert.That(await db.Audit.AnyAsync(x=>x.Action=="retention.pruned"),Is.True);
 }

 [Test]public async Task PrivacyErasureRemovesSecretsAndCustomerDataWhilePreservingAudit()
 {
  var(t,site)=await Customer();await using var db=Db();var d=new Device{TenantId=t.Id,SiteId=site.Id,Name="Private device",Active=false};db.Devices.Add(d);
  var job=new ManagedJob{TenantId=t.Id,DeviceId=d.Id,Name="Private job",LatestRevision=1};db.ManagedJobs.Add(job);db.ConfigurationRevisions.Add(new ConfigurationRevision{TenantId=t.Id,ManagedJobId=job.Id,Revision=1,EncryptedConfiguration="test-only-encrypted-placeholder"});
  db.Audit.Add(new AuditEvent{TenantId=t.Id,Actor="test",Action="customer.created",Resource=t.Id.ToString()});await db.SaveChangesAsync();
  Assert.ThrowsAsync<InvalidOperationException>(async()=>await Privacy.EraseCustomer(db,t.Id,true));
  var customer=await db.Customers.SingleAsync(x=>x.TenantId==t.Id);customer.Active=false;await db.SaveChangesAsync();
  await Privacy.EraseCustomer(db,t.Id,false);Assert.That(await db.ConfigurationRevisions.AnyAsync(x=>x.TenantId==t.Id),Is.True);
  await Privacy.EraseCustomer(db,t.Id,true);Assert.That(await db.Customers.AnyAsync(x=>x.TenantId==t.Id),Is.False);Assert.That(await db.ConfigurationRevisions.AnyAsync(x=>x.TenantId==t.Id),Is.False);Assert.That(await db.Devices.AnyAsync(x=>x.TenantId==t.Id),Is.False);
  Assert.That(await db.Audit.CountAsync(x=>x.TenantId==t.Id),Is.EqualTo(3));Assert.That((await db.Tenants.SingleAsync(x=>x.Id==t.Id)).Name,Does.StartWith("Erased tenant"));
 }

 [Test]public async Task OrdinaryDatabaseRoleCannotBypassRevisionImmutabilityOrEraseCustomer()
 {
  var(t,site)=await Customer();await using var owner=Db();var d=new Device{TenantId=t.Id,SiteId=site.Id,Active=false};owner.Devices.Add(d);var j=new ManagedJob{TenantId=t.Id,DeviceId=d.Id,LatestRevision=1};owner.ManagedJobs.Add(j);owner.ConfigurationRevisions.Add(new ConfigurationRevision{TenantId=t.Id,ManagedJobId=j.Id,Revision=1,EncryptedConfiguration="test-only-envelope"});var customer=await owner.Customers.SingleAsync(x=>x.TenantId==t.Id);customer.Active=false;await owner.SaveChangesAsync();
  var role="dt_limited_"+Guid.NewGuid().ToString("N");var password=Tokens.Create();
  // Role name/password are locally generated alphanumeric test fixtures, never user input.
  var quotedRole=new Npgsql.NpgsqlCommandBuilder().QuoteIdentifier(role);await owner.Database.OpenConnectionAsync();
  await using(var ddl=new Npgsql.NpgsqlCommand($"CREATE ROLE {quotedRole} LOGIN PASSWORD '{password}'; GRANT USAGE ON SCHEMA public TO {quotedRole}; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO {quotedRole}; GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO {quotedRole};",(Npgsql.NpgsqlConnection)owner.Database.GetDbConnection()))await ddl.ExecuteNonQueryAsync();
  try
  {
   var limitedConnection=new Npgsql.NpgsqlConnectionStringBuilder(connection){Username=role,Password=password}.ConnectionString;
   await using var limited=new ManagementDb(new DbContextOptionsBuilder<ManagementDb>().UseNpgsql(limitedConnection).Options,new TenantScope(new HttpContextAccessor()){Maintenance=true});
   Assert.ThrowsAsync<UnauthorizedAccessException>(async()=>await Privacy.EraseCustomer(limited,t.Id,true));
   await limited.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('dariatech.erase_tenant',{t.Id.ToString()},false)");
   Assert.ThrowsAsync<Npgsql.PostgresException>(async()=>await limited.ConfigurationRevisions.Where(x=>x.TenantId==t.Id).ExecuteDeleteAsync());
   Assert.That(await owner.ConfigurationRevisions.AnyAsync(x=>x.TenantId==t.Id),Is.True);
  }
  finally{Npgsql.NpgsqlConnection.ClearAllPools();await using var ddl=new Npgsql.NpgsqlCommand($"DROP OWNED BY {quotedRole}; DROP ROLE {quotedRole};",(Npgsql.NpgsqlConnection)owner.Database.GetDbConnection());await ddl.ExecuteNonQueryAsync();}
 }

 [Test]public async Task MonitoringUsesRealQuotaRetentionRepeatedFailureAndVerificationSignals()
 {
  var(t,site)=await Customer();await using var db=Db();var now=DateTimeOffset.UtcNow;var d=new Device{TenantId=t.Id,SiteId=site.Id,EngineReachable=true,LastHeartbeat=now};db.Devices.Add(d);db.Agents.Add(new DariaTech.Console.Data.Agent{TenantId=t.Id,DeviceId=d.Id,Version="0.2.0.0",CredentialHash=Tokens.Hash(Tokens.Create())});var job=new BackupJob{TenantId=t.Id,DeviceId=d.Id,LocalId="1",Name="Signals"};db.Jobs.Add(job);
  for(var i=0;i<3;i++)db.Runs.Add(new BackupRun{TenantId=t.Id,JobId=job.Id,LocalRunId="failure-"+i,Started=now.AddMinutes(-10+i),Completed=now.AddMinutes(-9+i),Status=RunStatus.Failed,QuotaTotalBytes=100,QuotaFreeBytes=1,QuotaError=false,RetentionError=true});
  db.Commands.Add(new RemoteCommand{TenantId=t.Id,DeviceId=d.Id,JobId=job.Id,Action=RemoteAction.VerifyBackup,Expires=now,Status="Failed"});await db.SaveChangesAsync();await Monitoring.Evaluate(db,new(),now);
  var codes=await db.Alerts.Where(x=>x.DeviceId==d.Id&&x.Resolved==null).Select(x=>x.Code).ToListAsync();Assert.That(codes,Does.Contain("RepeatedBackupFailures").And.Contain("StorageCapacityCritical").And.Contain("RetentionFailed").And.Contain("BackupVerificationFailed"));
  db.Runs.Add(new BackupRun{TenantId=t.Id,JobId=job.Id,LocalRunId="recovered",Started=now,Completed=now.AddMinutes(1),Status=RunStatus.Success,QuotaTotalBytes=100,QuotaFreeBytes=90,QuotaError=false,RetentionError=false});db.Commands.Add(new RemoteCommand{TenantId=t.Id,DeviceId=d.Id,JobId=job.Id,Action=RemoteAction.VerifyBackup,Expires=now.AddMinutes(1),Status="Completed"});await db.SaveChangesAsync();await Monitoring.Evaluate(db,new(),now.AddMinutes(2));
  Assert.That(await db.Alerts.CountAsync(x=>x.DeviceId==d.Id&&x.Resolved==null),Is.EqualTo(0));
 }
 [Test]public async Task AddDeviceWizardShowsPlatformPackageAndInstallCommandWithOneTimeToken()
 {
  var(t,site)=await Customer();await using var db=Db();var customer=await db.Customers.IgnoreQueryFilters().SingleAsync(x=>x.TenantId==t.Id);
  using var admin=await Login(UserRole.SuperAdmin);
  string Token(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  async Task<HttpResponseMessage> Enroll(string platform)=>await admin.PostAsync($"/Customer?id={customer.Id}&handler=Enroll",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"id",customer.Id.ToString()},{"siteId",site.Id.ToString()},{"validMinutes","30"},{"platform",platform},{"__RequestVerificationToken",Token(await admin.GetStringAsync($"/Customer?id={customer.Id}"))}}));
  var page=await admin.GetStringAsync($"/Customer?id={customer.Id}");
  Assert.That(page,Does.Contain("value=\"osx-arm64\"").And.Contain("value=\"linux-arm64\"").And.Contain("Betriebssystem des Geräts"));
  // No published release reachable: the token is still issued and the page points to the releases page.
  using(var fallback=await Enroll("linux-x64"))
  {
   var html=WebUtility.HtmlDecode(await fallback.Content.ReadAsStringAsync());Assert.That(fallback.StatusCode,Is.EqualTo(HttpStatusCode.OK));
   Assert.That(html,Does.Contain("Kein veröffentlichtes Agent-Paket").And.Contain("/releases"));
   Assert.That(fallback.Headers.CacheControl?.NoStore,Is.True);
  }
  var sha=new string('a',64);var url="https://github.com/DariaTech-de/DariaTech-Backup/releases/download/agent-v0.2.0/DariaTechBackupSetup-win-x64.exe";
  factory.Services.GetRequiredService<AgentReleases>().Use(new("0.2.0","agent-v0.2.0",DateTimeOffset.UtcNow,[new("win-x64","Windows","DariaTechBackupSetup-win-x64.exe",sha,42_000_000,url)],true,"https://github.com/DariaTech-de/DariaTech-Backup/releases/tag/agent-v0.2.0"));
  using(var windows=await Enroll("win-x64"))
  {
   var html=WebUtility.HtmlDecode(await windows.Content.ReadAsStringAsync());
   var token=Regex.Match(html,"<code class=\"token\">([0-9A-F]{64})</code>").Groups[1].Value;Assert.That(token,Has.Length.EqualTo(64));
   Assert.That(await db.EnrollmentTokens.IgnoreQueryFilters().CountAsync(x=>x.TenantId==t.Id&&x.TokenHash==Tokens.Hash(token)),Is.EqualTo(1));
Assert.That(html,Does.Contain("Agent 0.2.0 · Pilot").And.Contain(url).And.Contain(sha.ToUpperInvariant()).And.Contain($"-Value \"{token}\"").And.Contain("\"/console=https://localhost\",\"/tokenfile=$t\""));
  }
  // A platform without a published package falls back instead of showing another platform's file.
  using(var mac=await Enroll("osx-arm64")){var html=WebUtility.HtmlDecode(await mac.Content.ReadAsStringAsync());Assert.That(html,Does.Contain("Kein veröffentlichtes Agent-Paket").And.Not.Contain(url));}
  using(var invalid=await Enroll("freebsd-x64"))Assert.That(invalid.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest));
  using var customerAdmin=await Login(UserRole.CustomerAdmin,t.Id);
  using var denied=await customerAdmin.PostAsync($"/Customer?id={customer.Id}&handler=Enroll",new FormUrlEncodedContent(new Dictionary<string,string>{{"id",customer.Id.ToString()},{"siteId",site.Id.ToString()},{"validMinutes","30"},{"platform","win-x64"},{"__RequestVerificationToken",Token(await customerAdmin.GetStringAsync($"/Customer?id={customer.Id}"))}}));
  Assert.That(denied.StatusCode,Is.Not.EqualTo(HttpStatusCode.OK));
 }
 private sealed class TestFactory(string cs,string dir):WebApplicationFactory<Program>
 {
  protected override void ConfigureWebHost(IWebHostBuilder b)
  {
   b.UseEnvironment("Development");b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:Management",cs},{"Security:MasterKeyFile",Path.Combine(dir,"master.key")},{"Security:KeyDirectory",Path.Combine(dir,"keys")},{"Monitoring:PollSeconds","3600"},{"Commands:SigningKeyFile",Path.Combine(dir,"commands.pem")},{"Updates:PublicKeyFile",Path.Combine(dir,"commands.pem")},{"Agents:ApiBase","http://127.0.0.1:9"}}));
  }
 }
}
