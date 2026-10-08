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
 private sealed class TestFactory(string cs,string dir):WebApplicationFactory<Program>
 {
  protected override void ConfigureWebHost(IWebHostBuilder b)
  {
   b.UseEnvironment("Development");b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:Management",cs},{"Security:MasterKeyFile",Path.Combine(dir,"master.key")},{"Security:KeyDirectory",Path.Combine(dir,"keys")},{"Monitoring:PollSeconds","3600"},{"Commands:SigningKeyFile",Path.Combine(dir,"commands.pem")},{"Updates:PublicKeyFile",Path.Combine(dir,"commands.pem")}}));
  }
 }
}
