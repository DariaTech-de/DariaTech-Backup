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
  // An implausible run (e.g. a clock or time-zone error on the device) must not keep the device offline.
  var skewed=heartbeat with{Jobs=[new("1","Daily",null,run with{LocalRunId="run-future",Started=DateTimeOffset.UtcNow.AddHours(2),Completed=DateTimeOffset.UtcNow.AddHours(2)})]};
  var before=DateTimeOffset.UtcNow;using var tolerated=await c.PostAsJsonAsync("/api/v1/agent/heartbeat",skewed);Assert.That(tolerated.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  await using var db=Db();var job=await db.Jobs.SingleAsync(x=>x.DeviceId==identity.DeviceId);Assert.That(await db.Runs.CountAsync(x=>x.JobId==job.Id),Is.EqualTo(1));
  Assert.That((await db.Devices.SingleAsync(x=>x.Id==identity.DeviceId)).LastHeartbeat,Is.GreaterThanOrEqualTo(before.AddSeconds(-1)));
  // A report whose job list had to be shortened is no proof that a job was removed: known jobs stay monitored.
  using var two=await c.PostAsJsonAsync("/api/v1/agent/heartbeat",heartbeat with{Jobs=[new("1","Daily",null,run),new("2","Weekly",null,null)]});Assert.That(two.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  using var duplicated=await c.PostAsJsonAsync("/api/v1/agent/heartbeat",heartbeat with{Jobs=[new("1","Daily",null,run),new("2","Weekly",null,null),new("2","Weekly copy",null,null)]});Assert.That(duplicated.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  await using(var check=Db())
  {
   Assert.That(await check.Jobs.Where(x=>x.DeviceId==identity.DeviceId&&x.Active).Select(x=>x.LocalId).OrderBy(x=>x).ToListAsync(),Is.EqualTo(new[]{"1","2"}));
   Assert.That((await check.Devices.SingleAsync(x=>x.Id==identity.DeviceId)).InventoryIncompleteSince,Is.Not.Null);
  }
  using var removed=await c.PostAsJsonAsync("/api/v1/agent/heartbeat",heartbeat);Assert.That(removed.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  await using(var check=Db())
  {
   var weekly=await check.Jobs.SingleAsync(x=>x.DeviceId==identity.DeviceId&&x.LocalId=="2");Assert.That(weekly.Active,Is.False,"a complete report without the job shows it was removed on the device");
   Assert.That(await check.Audit.AnyAsync(x=>x.Action=="job.removed-from-device"&&x.Resource==weekly.Id.ToString()),Is.True);
   Assert.That((await check.Devices.SingleAsync(x=>x.Id==identity.DeviceId)).InventoryIncompleteSince,Is.Null);
  }
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
 [Test]public async Task WorkListShowsAgeNextStepAndWhoIsOnItAndOnlyMonitoringResolves()
 {
  var(t,s)=await Customer();await using var db=Db();var now=DateTimeOffset.UtcNow;
  var d=new Device{TenantId=t.Id,SiteId=s.Id,Name="Buchhaltung-PC",EngineReachable=true,LastHeartbeat=now};db.Devices.Add(d);db.Agents.Add(new DariaTech.Console.Data.Agent{TenantId=t.Id,DeviceId=d.Id,CredentialHash=Tokens.Hash(Tokens.Create())});
  var job=new BackupJob{TenantId=t.Id,DeviceId=d.Id,Name="Backup",LocalId="1",NextRun=now.AddHours(3)};db.Jobs.Add(job);
  db.Runs.Add(new BackupRun{TenantId=t.Id,JobId=job.Id,LocalRunId="ok",Started=now.AddDays(-3),Completed=now.AddDays(-3).AddMinutes(5),Status=RunStatus.Success});
  db.Runs.Add(new BackupRun{TenantId=t.Id,JobId=job.Id,LocalRunId="full",Started=now.AddHours(-1),Completed=now.AddMinutes(-50),Status=RunStatus.Failed,ErrorCode="BackupFailed",QuotaTotalBytes=100,QuotaFreeBytes=0,QuotaError=true});
  await db.SaveChangesAsync();await Monitoring.Evaluate(db,new(),now);
  using var admin=await Login(UserRole.Technician);
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  var page=WebUtility.HtmlDecode(await admin.GetStringAsync("/Alerts"));
  Assert.That(page,Does.Contain("Buchhaltung-PC").And.Contain("Backup fehlgeschlagen").And.Contain("Speicher kritisch voll").And.Contain("Speicher erweitern").And.Contain("vor 2 Tagen").And.Contain("Offen"));
  var alert=await db.Alerts.AsNoTracking().SingleAsync(x=>x.DeviceId==d.Id&&x.Code=="StorageCapacityCritical");
  using(var take=await admin.PostAsync("/Alerts?handler=Take",new FormUrlEncodedContent(new Dictionary<string,string>{{"alertId",alert.Id.ToString()},{"__RequestVerificationToken",Form(page)}})))Assert.That(take.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  Assert.That(WebUtility.HtmlDecode(await admin.GetStringAsync("/Alerts")),Does.Contain("In Bearbeitung"));
  Assert.That(await db.Alerts.AsNoTracking().Where(x=>x.Id==alert.Id).Select(x=>x.Resolved).SingleAsync(),Is.Null,"taking an alert does not resolve it");
  // The storage is extended and the next backup succeeds: monitoring resolves the alerts; they show as fixed.
  db.Runs.Add(new BackupRun{TenantId=t.Id,JobId=job.Id,LocalRunId="fixed",Started=now,Completed=now.AddMinutes(5),Status=RunStatus.Success,QuotaTotalBytes=100,QuotaFreeBytes=60,QuotaError=false});await db.SaveChangesAsync();
  await Monitoring.Evaluate(db,new(),now.AddMinutes(6));
  page=WebUtility.HtmlDecode(await admin.GetStringAsync("/Alerts"));
  Assert.That(page,Does.Contain("Behoben (letzte 7 Tage)").And.Contain("behoben"));
 }
 [Test]public async Task SharedSettingsChangeOnlyTheTickedSettingsAfterAPreview()
 {
  var(t,site)=await Customer();await using var db=Db();var customer=await db.Customers.IgnoreQueryFilters().SingleAsync(x=>x.TenantId==t.Id);
  var a=new Device{TenantId=t.Id,SiteId=site.Id,Name="PC-A"};var b=new Device{TenantId=t.Id,SiteId=site.Id,Name="PC-B"};db.Devices.AddRange(a,b);await db.SaveChangesAsync();
  using var admin=await Login(UserRole.Administrator);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  ManagedBackupDefinition Def(string source,string passphrase)=>new("Dokumente",[source],"webdav://storage.example/dav/k/"+passphrase[..4],passphrase,new(){{"use-ssl","true"},{"auth-username","u"},{"auth-password","p"}},30,[new(false,"*.tmp")],null);
  var jobs=new List<ManagedJob>();
  foreach(var (device,def) in new[]{(a,Def("C:\\Users\\Anna\\Documents","anna-passphrase-12345")),(b,Def("C:\\Users\\Ben\\Desktop","benb-passphrase-12345"))})
  {using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{device.Id}/managed-jobs",new ConfigurationInput(0,def));jobs.Add((await created.Content.ReadFromJsonAsync<ManagedJob>())!);}
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  var url=$"/SharedSettings?id={customer.Id}";var page=WebUtility.HtmlDecode(await admin.GetStringAsync(url));Assert.That(page,Does.Contain("PC-A").And.Contain("PC-B").And.Contain("Manuell"));
  List<KeyValuePair<string,string>> Fields(bool confirm){var f=new List<KeyValuePair<string,string>>{new("id",customer.Id.ToString()),new("JobIds",jobs[0].Id.ToString()),new("JobIds",jobs[1].Id.ToString()),new("ChangeSchedule","true"),new("FirstRun","2026-11-02T22:00"),new("RepeatHours","24"),new("StaggerMinutes","30"),new("ChangeRetention","true"),new("KeepVersions","90"),new("__RequestVerificationToken",Form(page))};
   foreach(var day in new[]{"Monday","Tuesday","Wednesday","Thursday","Friday"})f.Add(new("Days",day));if(confirm)f.Add(new("Confirm","true"));return f;}
  using(var preview=await admin.PostAsync(url+"&handler=Preview",new FormUrlEncodedContent(Fields(false))))
  {
   var html=WebUtility.HtmlDecode(await preview.Content.ReadAsStringAsync());Assert.That(html,Does.Contain("Vorschau – diese Backups ändern sich").And.Contain("2 Backups auf 2 Gerät(en)").And.Contain("90 Versionen"));
  }
  Assert.That(await db.ConfigurationRevisions.CountAsync(x=>x.ManagedJobId==jobs[0].Id),Is.EqualTo(1),"a preview changes nothing");
  using(var unconfirmed=await admin.PostAsync(url+"&handler=Apply",new FormUrlEncodedContent(Fields(false))))Assert.That(WebUtility.HtmlDecode(await unconfirmed.Content.ReadAsStringAsync()),Does.Contain("Bitte die Änderung"));
  using(var applied=await admin.PostAsync(url+"&handler=Apply",new FormUrlEncodedContent(Fields(true))))Assert.That(WebUtility.HtmlDecode(await applied.Content.ReadAsStringAsync()),Does.Contain("wird beim nächsten Kontakt des Geräts angewendet"));
  var secrets=factory.Services.GetRequiredService<ISecretStore>();
  ManagedBackupDefinition Latest(Guid id){var r=db.ConfigurationRevisions.AsNoTracking().Where(x=>x.ManagedJobId==id).OrderByDescending(x=>x.Revision).First();return DariaTech.Console.Api.ConfigurationApi.Read(secrets,r);}
  var first=Latest(jobs[0].Id);var second=Latest(jobs[1].Id);
  Assert.That(first.Sources,Is.EqualTo(new[]{"C:\\Users\\Anna\\Documents"}));Assert.That(second.Sources,Is.EqualTo(new[]{"C:\\Users\\Ben\\Desktop"}),"individual sources are kept");
  Assert.That(second.Passphrase,Is.EqualTo("benb-passphrase-12345"));Assert.That(first.Filters.Single().Expression,Is.EqualTo("*.tmp"));Assert.That(first.KeepVersions,Is.EqualTo(90));
  Assert.That(first.Schedule!.Days,Has.Length.EqualTo(5));Assert.That(second.Schedule!.Start-first.Schedule.Start,Is.EqualTo(TimeSpan.FromMinutes(30)),"start times are staggered");
  if(DariaTech.Console.Services.Ui.Zone!=TimeZoneInfo.Utc)Assert.That(first.Schedule.Start,Is.EqualTo(new DateTimeOffset(2026,11,2,21,0,0,TimeSpan.Zero)),"22:00 German winter time");
  var stored=await db.ManagedJobs.AsNoTracking().SingleAsync(x=>x.Id==jobs[0].Id);Assert.That(stored.LatestRevision,Is.EqualTo(2));Assert.That(stored.AppliedRevision,Is.Zero,"saved is not applied");
  Assert.That(stored.ScheduleRepeatHours,Is.EqualTo(24));Assert.That(stored.KeepVersions,Is.EqualTo(90));
 }
 [Test]public async Task ContractEndStepsAreSeparateAndArchivesStayUntilDeletionIsApproved()
 {
  var(t,site)=await Customer();await using var db=Db();var customer=await db.Customers.IgnoreQueryFilters().SingleAsync(x=>x.TenantId==t.Id);
  var pc=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
  using(var e=await pc.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),"PC-Ende","Windows 11","0.3.1","win-x64")))
  {var identity=(await e.Content.ReadFromJsonAsync<EnrollmentResponse>())!;pc.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);pc.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());}
  var deviceId=Guid.Parse(pc.DefaultRequestHeaders.GetValues("X-Device-Id").Single());
  using var admin=await Login(UserRole.SuperAdmin);using var portal=await Login(UserRole.CustomerAdmin,t.Id);
  using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  using(var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{deviceId}/managed-jobs",new ConfigurationInput(0,new ManagedBackupDefinition("Daten",["C:\\Daten"],"webdav://storage.example/dav/ende","contract-end-passphrase-1",new(){{"use-ssl","true"},{"auth-username","u"},{"auth-password","p"}},30,[],null))))Assert.That(created.StatusCode,Is.EqualTo(HttpStatusCode.Created));
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  var url=$"/Customer?id={customer.Id}";
  async Task<string> Step(string step,bool confirm=true,Dictionary<string,string>? extra=null)
  {
   var fields=new Dictionary<string,string>{{"id",customer.Id.ToString()},{"step",step},{"__RequestVerificationToken",Form(await admin.GetStringAsync(url))}};if(confirm)fields["confirm"]="true";foreach(var (k,v) in extra??[])fields[k]=v;
   using var response=await admin.PostAsync(url+"&handler=Contract",new FormUrlEncodedContent(fields));
   return WebUtility.HtmlDecode(response.StatusCode==HttpStatusCode.Redirect?await admin.GetStringAsync(response.Headers.Location!.ToString()):await response.Content.ReadAsStringAsync());
  }
  HeartbeatRequest Beat()=>new("0.3.1","Windows 11",true,[],null,"win-x64");
  // Archiving ends monitoring and portal access to new devices, but neither stops the device nor deletes anything.
  Assert.That(await Step("archive",confirm:false),Does.Contain("ausdrücklich bestätigen"));
  var page=await Step("archive");Assert.That(page,Does.Contain("archiviert – keine Überwachung").And.Contain("PC-Ende – sichert weiter"));
  using(var hb=await pc.PostAsJsonAsync("/api/v1/agent/heartbeat",Beat()))Assert.That(hb.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized),"archived: no management, local schedules untouched");
  Assert.That(await db.Agents.AsNoTracking().Where(x=>x.DeviceId==deviceId).Select(x=>x.Revoked).SingleAsync(),Is.False);
  // Stopping local backups is its own step and reaches the archived customer's device.
  page=await Step("stop");Assert.That(page,Does.Contain("Stopp angefordert – noch nicht bestätigt"));
  using(var hb=await pc.PostAsJsonAsync("/api/v1/agent/heartbeat",Beat()))Assert.That(hb.StatusCode,Is.EqualTo(HttpStatusCode.Gone));
  using(var ok=await pc.PostAsync("/api/v1/agent/retired",null))Assert.That(ok.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  Assert.That(await Step("lock"),Does.Contain("gesperrt"));
  using(var locked=await portal.GetAsync("/"))Assert.That(locked.StatusCode,Is.Not.EqualTo(HttpStatusCode.OK),"customer users are locked out");
  // Deletion of archives can only be approved after the agreed retention, and the software deletes nothing itself.
  var today=DateOnly.FromDateTime(DateTime.UtcNow);
  await Step("dates",extra:new(){{"contractEnd",today.AddDays(-1).ToString("yyyy-MM-dd")},{"retainUntil",today.AddDays(90).ToString("yyyy-MM-dd")}});
  Assert.That(await Step("deletion"),Does.Contain("erst nach dem vereinbarten Aufbewahrungsende"));
  Assert.That(await Step("storage"),Does.Contain("Manuell bestätigt"));
  page=await Step("close");Assert.That(page,Does.Contain("Abgeschlossen").And.Contain("nicht freigegeben – Archive bleiben erhalten"));
  Assert.That(await db.ManagedJobs.AnyAsync(x=>x.TenantId==t.Id),Is.True,"backup definitions stay recoverable");
  foreach(var action in new[]{"contract.archive","contract.stop","contract.lock","contract.dates","contract.storage","contract.close"})Assert.That(await db.Audit.AnyAsync(x=>x.TenantId==t.Id&&x.Action==action),Is.True,action);
  Assert.That(await db.Audit.AnyAsync(x=>x.TenantId==t.Id&&x.Action=="contract.deletion"),Is.False);
  pc.Dispose();
 }
 [Test]public async Task RotatedTemplateCredentialsReachOnlySelectedJobsBelowTheTemplate()
 {
  var(t,site)=await Customer();await using var db=Db();var customer=await db.Customers.IgnoreQueryFilters().SingleAsync(x=>x.TenantId==t.Id);
  var a=new Device{TenantId=t.Id,SiteId=site.Id,Name="PC-Rot"};db.Devices.Add(a);
  var secrets=factory.Services.GetRequiredService<ISecretStore>();
  var template=new DestinationTemplate{TenantId=t.Id,Name="Kunden-Nextcloud "+Guid.NewGuid().ToString("N")[..6],TargetUrl="webdav://cloud.kunde.example/dav/backup"};
  DestinationTemplates.SetOptions(secrets,template,new(){{"use-ssl","true"},{"auth-username","backup"},{"auth-password","rotated-password-2"}});db.DestinationTemplates.Add(template);await db.SaveChangesAsync();
  using var admin=await Login(UserRole.Administrator);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  var opts=new Dictionary<string,string>{{"use-ssl","true"},{"auth-username","backup"},{"auth-password","old-password-1"}};
  var below=new ManagedBackupDefinition("Unter Vorlage",["C:\\Daten"],"webdav://cloud.kunde.example/dav/backup/K1/PC-Rot/Daten-abc123","rotation-passphrase-1",opts,30,[],null);
  var elsewhere=below with{Name="Anderes Ziel",TargetUrl="webdav://other.example/dav/x"};
  var ids=new List<Guid>();foreach(var d in new[]{below,elsewhere}){using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{a.Id}/managed-jobs",new ConfigurationInput(0,d));ids.Add((await created.Content.ReadFromJsonAsync<ManagedJob>())!.Id);}
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  var url=$"/SharedSettings?id={customer.Id}";var page=await admin.GetStringAsync(url);
  var fields=new List<KeyValuePair<string,string>>{new("id",customer.Id.ToString()),new("JobIds",ids[0].ToString()),new("JobIds",ids[1].ToString()),new("ChangeCredentials","true"),new("TemplateId",template.Id.ToString()),new("Confirm","true"),new("__RequestVerificationToken",Form(page))};
  using(var applied=await admin.PostAsync(url+"&handler=Apply",new FormUrlEncodedContent(fields)))
  {var html=WebUtility.HtmlDecode(await applied.Content.ReadAsStringAsync());Assert.That(html,Does.Contain("Unter Vorlage: gespeichert").And.Not.Contain("Anderes Ziel: gespeichert").And.Not.Contain("rotated-password-2"));}
  ManagedBackupDefinition Latest(Guid id){var r=db.ConfigurationRevisions.AsNoTracking().Where(x=>x.ManagedJobId==id).OrderByDescending(x=>x.Revision).First();return DariaTech.Console.Api.ConfigurationApi.Read(secrets,r);}
  Assert.That(Latest(ids[0]).BackendOptions["auth-password"],Is.EqualTo("rotated-password-2"));Assert.That(Latest(ids[0]).TargetUrl,Is.EqualTo(below.TargetUrl),"the job keeps its own folder");
  Assert.That(Latest(ids[1]).BackendOptions["auth-password"],Is.EqualTo("old-password-1"),"a job outside the template is not touched");
  db.DestinationTemplates.Remove(await db.DestinationTemplates.SingleAsync(x=>x.Id==template.Id));await db.SaveChangesAsync();
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
  // The connection test of a template runs on the device against the customer/device folder the job writes into.
  using(var tested=await admin.PostAsync("/BackupEdit?handler=TestDestination&createFolder=true",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"DeviceId",device.Id.ToString()},{"DestinationMode","template"},{"TemplateId",template.Id.ToString()},{"__RequestVerificationToken",Token(form)}})))
  {
   using var answer=System.Text.Json.JsonDocument.Parse(await tested.Content.ReadAsStringAsync());var commandId=answer.RootElement.GetProperty("id").GetGuid();
   var record=await db.Commands.AsNoTracking().SingleAsync(x=>x.Id==commandId);Assert.That(record.DeviceId,Is.EqualTo(device.Id));
   var payload=factory.Services.GetRequiredService<ISecretStore>().Unprotect(t.Id,$"command:{commandId}",record.EncryptedPayload);
   var command=System.Text.Json.JsonSerializer.Deserialize<DeviceCommand>(Convert.FromBase64String(payload))!;
   Assert.That(command.Test!.TargetUrl,Is.EqualTo($"ssh://storage.dariatech.example:2222/backups/{number}/Template-worker"));Assert.That(command.Test.CreateFolder,Is.True);
  }
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
  Assert.That(await db.Audit.CountAsync(x=>x.Resource==id.ToString()),Is.EqualTo(5));
  Assert.That(await db.Audit.AnyAsync(x=>x.Resource==id.ToString()&&x.Action=="command.receipt-conflict-accepted"),Is.True,"a contradicting receipt is visible in the command history");
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
 [Test]public async Task OverdueFollowsTheScheduleAbsenceAndManagedJobState()
 {
  var(t,site)=await Customer();await using var db=Db();
  // Monday 08:00 UTC; the last successful run was Friday 22:05.
  var monday=new DateTimeOffset(2026,10,19,8,0,0,TimeSpan.Zero);var friday=monday.AddDays(-3).AddHours(14).AddMinutes(5);
  Device Pc(string name){var d=new Device{TenantId=t.Id,SiteId=site.Id,Name=name,EngineReachable=true,LastHeartbeat=monday};db.Devices.Add(d);db.Agents.Add(new DariaTech.Console.Data.Agent{TenantId=t.Id,DeviceId=d.Id,CredentialHash=Tokens.Hash(Tokens.Create())});return d;}
  (BackupJob Job,ManagedJob Managed) Job(Device d,string local,int? repeat,int days,DateTimeOffset? success)
  {
   var job=new BackupJob{TenantId=t.Id,DeviceId=d.Id,LocalId=local,Name="Dokumente",Ownership="Managed"};db.Jobs.Add(job);
   var managed=new ManagedJob{TenantId=t.Id,DeviceId=d.Id,Name="Dokumente",LatestRevision=1,AppliedRevision=1,LastReportedRevision=1,LocalJobId=local,Status="Applied",ScheduleRecorded=true,ScheduleRepeatHours=repeat,ScheduleDays=days};db.ManagedJobs.Add(managed);
   if(success is {} at)db.Runs.Add(new BackupRun{TenantId=t.Id,JobId=job.Id,LocalRunId="ok-"+local,Started=at.AddMinutes(-5),Completed=at,Status=RunStatus.Success});
   return (job,managed);
  }
  const int weekdays=0b0111110;
  var office=Pc("Büro-1");Job(office,"1",24,weekdays,friday);                 // Mon-Fri daily: not overdue on Monday morning
  var daily=Pc("Büro-2");Job(daily,"1",24,127,friday);                       // every day: overdue on Monday
  var manual=Pc("Büro-3");Job(manual,"1",null,0,null);                       // no schedule, never backed up
  var away=Pc("Laptop");away.LastHeartbeat=monday.AddDays(-5);away.AbsentUntil=monday.AddDays(3);Job(away,"1",24,127,monday.AddDays(-5));
  var broken=Pc("Büro-4");var (_,failed)=Job(broken,"1",24,127,monday.AddHours(-2));failed.Status="Failed";
  var (_,gone)=Job(broken,"2",24,127,null);db.Jobs.Remove(db.Jobs.Local.Single(x=>x.DeviceId==broken.Id&&x.LocalId=="2"));
  await db.SaveChangesAsync();await Monitoring.Evaluate(db,new(),monday);
  async Task<string[]> Codes(Device d)=>await db.Alerts.Where(x=>x.DeviceId==d.Id&&x.Resolved==null).Select(x=>x.Code).OrderBy(x=>x).ToArrayAsync();
  Assert.That(await Codes(office),Is.Empty,"a weekend without scheduled runs is not overdue");
  Assert.That(await Codes(daily),Is.EqualTo(new[]{"BackupOverdue"}));
  Assert.That(await Codes(manual),Is.EqualTo(new[]{"BackupManualOnly"}));
  Assert.That(await Codes(away),Is.Empty,"a planned absence suppresses offline and overdue");
  Assert.That(await Codes(broken),Is.EqualTo(new[]{"ConfigurationFailed","ManagedJobMissing"}));
  // After the absence ends, normal monitoring applies again by itself.
  await Monitoring.Evaluate(db,new(),monday.AddDays(3).AddMinutes(1));
  Assert.That(await Codes(away),Is.EqualTo(new[]{"BackupOverdue","DeviceOffline"}));
  Assert.That(await Codes(office),Is.EqualTo(new[]{"BackupOverdue","DeviceOffline"}),"Thursday without a run since Friday is overdue");
 }
 [Test]public async Task FivePcCustomerIsInSetupUntilEveryDeviceIsProvenAndARestoreIsConfirmed()
 {
  var(t,site)=await Customer();await using var db=Db();var customer=await db.Customers.IgnoreQueryFilters().SingleAsync(x=>x.TenantId==t.Id);
  using var admin=await Login(UserRole.SuperAdmin);
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  async Task<string> Post(string url,Dictionary<string,string> fields,string formPage)
  {
   fields["__RequestVerificationToken"]=Form(await admin.GetStringAsync(formPage));using var response=await admin.PostAsync(url,new FormUrlEncodedContent(fields));
   Assert.That((int)response.StatusCode,Is.LessThan(400));return WebUtility.HtmlDecode(response.StatusCode==HttpStatusCode.Redirect?await admin.GetStringAsync(response.Headers.Location!.ToString()):await response.Content.ReadAsStringAsync());
  }
  var customerPage=$"/Customer?id={customer.Id}";
  await Post($"{customerPage}&handler=Expected",new(){{"id",customer.Id.ToString()},{"expectedDevices","5"}},customerPage);
  // Five single-use codes in one step, each with its own command.
  var issued=await Post($"{customerPage}&handler=Enroll",new(){{"id",customer.Id.ToString()},{"siteId",site.Id.ToString()},{"validMinutes","60"},{"platform","win-x64"},{"allowManaged","true"},{"allowRemote","true"},{"count","5"}},customerPage);
  var codes=Regex.Matches(issued,"<code class=\"token\">([^<]{64})</code>").Select(x=>x.Groups[1].Value).ToList();
  Assert.That(codes.Distinct().Count(),Is.EqualTo(5));Assert.That(issued,Does.Contain("5 Einmal-Codes").And.Contain("genau <strong>ein</strong> Gerät"));
  var pcs=new List<(HttpClient Client,EnrollmentResponse Id)>();
  foreach(var code in codes.Take(4))
  {
   var client=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
   using var e=await client.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(code,$"PC-{pcs.Count+1}","Windows 11","0.3.1","win-x64"));Assert.That(e.StatusCode,Is.EqualTo(HttpStatusCode.OK));
   var identity=(await e.Content.ReadFromJsonAsync<EnrollmentResponse>())!;client.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);client.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());pcs.Add((client,identity));
  }
  // A code is used up: a second installation with the same code is refused.
  using(var again=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")}))using(var reuse=await again.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(codes[0],"PC-1-copy","Windows 11","0.3.1","win-x64")))Assert.That(reuse.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
  using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  var definition=new ManagedBackupDefinition("Dokumente",["C:\\Users\\Public\\Documents"],"webdav://storage.example/dav/kunde/pc","five-pc-passphrase-24680",new(){{"use-ssl","true"},{"auth-username","u"},{"auth-password","p"}},30,[],new(DateTimeOffset.UtcNow.AddHours(2),24,Enum.GetValues<DayOfWeek>()));
  var managed=new List<ManagedJob>();
  for(var i=0;i<4;i++)
  {
   using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{pcs[i].Id.DeviceId}/managed-jobs",new ConfigurationInput(0,definition with{TargetUrl=definition.TargetUrl+(i+1)}));managed.Add((await created.Content.ReadFromJsonAsync<ManagedJob>())!);
  }
  var now=DateTimeOffset.UtcNow;
  // PC-1..3 apply their job; PC-4 is only connected. PC-1 and PC-2 finish a first backup; PC-3's first backup fails.
  for(var i=0;i<3;i++)using(var applied=await pcs[i].Client.PostAsJsonAsync($"/api/v1/agent/configurations/{managed[i].Id}/receipt",new ConfigurationReceipt(1,"1","Applied")))Assert.That(applied.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var runs=new RunReport?[]{new("r1",now.AddMinutes(-30),now.AddMinutes(-5),RunStatus.Success,10,1,10,null,null),new("r2",now.AddMinutes(-30),now.AddMinutes(-5),RunStatus.Success,10,1,10,null,null),new("r3",now.AddMinutes(-30),now.AddMinutes(-5),RunStatus.Failed,null,null,null,null,"BackupFailed"),null};
  for(var i=0;i<4;i++)using(var hb=await pcs[i].Client.PostAsJsonAsync("/api/v1/agent/heartbeat",new HeartbeatRequest("0.3.1","Windows 11",true,i<3?[new("1","Dokumente",now.AddHours(2),runs[i])]:[],null,"win-x64")))Assert.That(hb.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var page=WebUtility.HtmlDecode(await admin.GetStringAsync(customerPage));
  Assert.That(page,Does.Contain("In Einrichtung").And.Contain("5 Geräte vorgesehen, 4 verbunden, 3 konfiguriert, 2 erfolgreich gesichert, 0 mit geprüftem Restore").And.Contain("1 vorgesehenes Gerät ist noch nicht registriert").And.Contain("1 Registrierungscode noch offen"));
  Assert.That(page,Does.Contain("Fehlgeschlagene Erstsicherung prüfen").And.Contain("Backup-Job anlegen bzw. Übernahme abwarten").Or.Contain("Warten, bis das Gerät die Konfiguration übernimmt"));
  // A completed restore is technical evidence only; the manual check makes it "Restore geprüft".
  var job1=await db.Jobs.AsNoTracking().SingleAsync(x=>x.DeviceId==pcs[0].Id.DeviceId&&x.LocalId=="1");
  var restore=new RemoteCommand{TenantId=t.Id,DeviceId=job1.DeviceId,JobId=job1.Id,Action=RemoteAction.Restore,Expires=now.AddMinutes(10),Status="Completed",RequestedBy="test"};db.Commands.Add(restore);await db.SaveChangesAsync();
  Assert.That(WebUtility.HtmlDecode(await admin.GetStringAsync(customerPage)),Does.Contain("technisch abgeschlossen – Prüfung offen"));
  var actions=$"/JobActions?id={job1.Id}";
  Assert.That(await Post($"{actions}&handler=CheckRestore",new(){{"JobId",job1.Id.ToString()},{"commandId",restore.Id.ToString()}},actions),Does.Contain("Bitte bestätigen"));
  await Post($"{actions}&handler=CheckRestore",new(){{"JobId",job1.Id.ToString()},{"commandId",restore.Id.ToString()},{"confirm","true"}},actions);
  page=WebUtility.HtmlDecode(await admin.GetStringAsync(customerPage));
  Assert.That(page,Does.Contain("1 mit geprüftem Restore").And.Contain("In Einrichtung"),"one accepted device does not make the customer operational");
  Assert.That(await db.Audit.AnyAsync(x=>x.Action=="restore.checked-usable"&&x.Resource==restore.Id.ToString()),Is.True);
  var device=WebUtility.HtmlDecode(await admin.GetStringAsync($"/Device?id={pcs[0].Id.DeviceId}"));
  Assert.That(device,Does.Contain("Schutzstatus").And.Contain("abgenommen").And.Contain("Restore-Test bestätigt (manuell)").And.Contain("Speicher vom Gerät erreicht"));
  Assert.That(WebUtility.HtmlDecode(await admin.GetStringAsync($"/Device?id={pcs[3].Id.DeviceId}")),Does.Contain("noch nicht abgenommen"),"a connection alone is no protection");
  foreach(var (client,_) in pcs)client.Dispose();
 }
 [Test]public async Task AddDeviceWizardShowsPlatformPackageAndInstallCommandWithOneTimeToken()
 {
  var(t,site)=await Customer();await using var db=Db();var customer=await db.Customers.IgnoreQueryFilters().SingleAsync(x=>x.TenantId==t.Id);
  using var admin=await Login(UserRole.SuperAdmin);
  string Token(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  async Task<HttpResponseMessage> Enroll(string platform)=>await admin.PostAsync($"/Customer?id={customer.Id}&handler=Enroll",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"id",customer.Id.ToString()},{"siteId",site.Id.ToString()},{"validMinutes","30"},{"platform",platform},{"allowManaged","true"},{"allowRemote","true"},{"__RequestVerificationToken",Token(await admin.GetStringAsync($"/Customer?id={customer.Id}"))}}));
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
Assert.That(html,Does.Contain("Agent 0.2.0 · Pilot").And.Contain(url).And.Contain(sha.ToUpperInvariant()).And.Contain($"-Value \"{token}\"").And.Contain("\"/console=https://localhost\",\"/tokenfile=$t\",\"/allowmanaged=1\",\"/allowremote=1\",\"/commandkeyfile=$k\"").And.Contain("Backup-Jobs aus der Konsole zulassen"));
   // The pinned key is the Console's own public command key.
   Assert.That(html,Does.Contain(factory.Services.GetRequiredService<CommandSigning>().PublicKeyPem.Trim()));
  }
  // A platform without a published package falls back instead of showing another platform's file.
  using(var mac=await Enroll("osx-arm64")){var html=WebUtility.HtmlDecode(await mac.Content.ReadAsStringAsync());Assert.That(html,Does.Contain("Kein veröffentlichtes Agent-Paket").And.Not.Contain(url));}
  using(var invalid=await Enroll("freebsd-x64"))Assert.That(invalid.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest));
  using var customerAdmin=await Login(UserRole.CustomerAdmin,t.Id);
  using var denied=await customerAdmin.PostAsync($"/Customer?id={customer.Id}&handler=Enroll",new FormUrlEncodedContent(new Dictionary<string,string>{{"id",customer.Id.ToString()},{"siteId",site.Id.ToString()},{"validMinutes","30"},{"platform","win-x64"},{"__RequestVerificationToken",Token(await customerAdmin.GetStringAsync($"/Customer?id={customer.Id}"))}}));
  Assert.That(denied.StatusCode,Is.Not.EqualTo(HttpStatusCode.OK));
 }
 [Test]public async Task StorageLocationsAreScopedPerCustomerWithCustomerDefaultFirst()
 {
  var(a,siteA)=await Customer();var(b,siteB)=await Customer();await using var db=Db();
  var deviceA=new Device{TenantId=a.Id,SiteId=siteA.Id,Name="A worker"};var deviceB=new Device{TenantId=b.Id,SiteId=siteB.Id,Name="B worker"};db.Devices.AddRange(deviceA,deviceB);await db.SaveChangesAsync();
  using var admin=await Login(UserRole.SuperAdmin);
  string Token(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  async Task<HttpResponseMessage> Save(string name,Guid? scope,string host)=>await admin.PostAsync("/Targets?handler=Save",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"Name",name},{"Scope",scope?.ToString()??""},{"IsDefault","true"},{"DestinationKey","webdav"},{"dest.webdav.host",host},{"dest.webdav.path","backup"},
   {"dest.webdav.opt.use-ssl","true"},{"dest.webdav.opt.auth-username","u"},{"dest.webdav.opt.auth-password","scoped-secret-"+name},{"__RequestVerificationToken",Token(await admin.GetStringAsync("/Targets"))}}));
  var tag=Guid.NewGuid().ToString("N")[..6];
  using(var g=await Save("Alle "+tag,null,"central.example"))Assert.That(g.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  using(var own=await Save("Nextcloud A "+tag,a.Id,"cloud.customer-a.example"))Assert.That(own.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  // The same name may exist once per scope.
  using(var same=await Save("Alle "+tag,a.Id,"other.customer-a.example"))Assert.That(same.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  var global=await db.DestinationTemplates.AsNoTracking().SingleAsync(x=>x.Name=="Alle "+tag&&x.TenantId==null);
  var customerA=await db.DestinationTemplates.AsNoTracking().SingleAsync(x=>x.Name=="Nextcloud A "+tag);
  Assert.That(customerA.TenantId,Is.EqualTo(a.Id));
  Assert.That(await db.DestinationTemplates.CountAsync(x=>x.TenantId==a.Id&&x.IsDefault),Is.EqualTo(1),"one default per customer");
  // Options are encrypted for the customer: decrypting them for another tenant fails.
  var store=factory.Services.GetRequiredService<ISecretStore>();
  Assert.That(DestinationTemplates.Options(store,customerA)["auth-password"],Is.EqualTo("scoped-secret-Nextcloud A "+tag));
  Assert.That(()=>store.Unprotect(Guid.Empty,$"destination-template:{customerA.Id}",customerA.EncryptedOptions),Throws.Exception);
  var formA=await admin.GetStringAsync($"/BackupEdit?deviceId={deviceA.Id}");var formB=await admin.GetStringAsync($"/BackupEdit?deviceId={deviceB.Id}");
  var selectedA=Regex.Match(formA,"<option value=\"([0-9a-f-]{36})\" selected=\"selected\"").Groups[1].Value;
  Assert.That(await db.DestinationTemplates.AsNoTracking().Where(x=>x.TenantId==a.Id&&x.IsDefault).Select(x=>x.Id.ToString()).SingleAsync(),Is.EqualTo(selectedA),"the customer's default is preselected");
  Assert.That(formA,Does.Contain(global.Id.ToString()));
  Assert.That(formB,Does.Contain(global.Id.ToString()).And.Not.Contain(customerA.Id.ToString()),"other customers never see it");
  // Using another customer's location is refused like a missing one.
  using var foreign=await admin.PostAsync("/BackupEdit",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"DeviceId",deviceB.Id.ToString()},{"Name","Foreign"},{"Provider","Files"},{"Sources","/srv"},{"DestinationMode","template"},{"TemplateId",customerA.Id.ToString()},
   {"Passphrase","foreign-passphrase-123456"},{"KeepVersions","30"},{"RepeatHours","24"},{"__RequestVerificationToken",Token(formB)}}));
  Assert.That(foreign.StatusCode,Is.EqualTo(HttpStatusCode.OK));Assert.That(await db.ManagedJobs.AnyAsync(x=>x.DeviceId==deviceB.Id),Is.False);
  var customerPage=await admin.GetStringAsync($"/Customer?id={await db.Customers.Where(x=>x.TenantId==a.Id).Select(x=>x.Id).SingleAsync()}");
  Assert.That(WebUtility.HtmlDecode(customerPage),Does.Contain("Nextcloud A "+tag).And.Contain("Nur dieser Kunde · Standard").And.Not.Contain("scoped-secret"));
  db.DestinationTemplates.RemoveRange(await db.DestinationTemplates.Where(x=>x.Name.EndsWith(tag)).ToListAsync());await db.SaveChangesAsync();
 }
 [Test]public async Task BackupOfAReplacedDeviceIsRecoverableOnAnotherDeviceWithoutBackingUpIntoIt()
 {
  var(t,site)=await Customer();var(other,otherSite)=await Customer();
  async Task<(HttpClient Client,EnrollmentResponse Identity)> Enroll(Tenant tenant,Site s,string name)
  {
   var client=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
   using var e=await client.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(tenant,s),name,"Windows","0.2.0"));var identity=(await e.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
   client.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);client.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());return (client,identity);
  }
  var (oldPc,oldId)=await Enroll(t,site,"Alter PC");var (newPc,newId)=await Enroll(t,site,"Neuer PC");var (foreignPc,foreignId)=await Enroll(other,otherSite,"Fremd");
  using(oldPc)using(newPc)using(foreignPc){
  using var admin=await Login(UserRole.SuperAdmin);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  var definition=new ManagedBackupDefinition("Buchhaltung",["C:\\Daten"],"webdav://cloud.example/backup/K1/AlterPC/Buchhaltung","recovery-passphrase-987654",new(){{"use-ssl","true"},{"auth-username","u"},{"auth-password","p"}},30,[],new(DateTimeOffset.UtcNow,24,Enum.GetValues<DayOfWeek>()));
  using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{oldId.DeviceId}/managed-jobs",new ConfigurationInput(0,definition));
  var source=(await created.Content.ReadFromJsonAsync<ManagedJob>())!;
  using(var applied=await oldPc.PostAsJsonAsync($"/api/v1/agent/configurations/{source.Id}/receipt",new ConfigurationReceipt(1,"3","Applied")))Assert.That(applied.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  await using var db=Db();var customer=await db.Customers.Where(x=>x.TenantId==t.Id).Select(x=>x.Id).SingleAsync();
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  async Task<HttpResponseMessage> Recover(Guid target)=>await admin.PostAsync($"/Customer?id={customer}&handler=Recover",new FormUrlEncodedContent(new Dictionary<string,string>{
   {"id",customer.ToString()},{"sourceJobId",source.Id.ToString()},{"targetDeviceId",target.ToString()},{"__RequestVerificationToken",Form(await admin.GetStringAsync($"/Customer?id={customer}"))}}));
  var page=WebUtility.HtmlDecode(await admin.GetStringAsync($"/Customer?id={customer}"));
  Assert.That(page,Does.Contain("Backups & Wiederherstellung").And.Contain("Buchhaltung").And.Contain("Auf anderes Gerät wiederherstellen").And.Not.Contain("recovery-passphrase"));
  using(var foreign=await Recover(foreignId.DeviceId)){Assert.That(WebUtility.HtmlDecode(await foreign.Content.ReadAsStringAsync()),Does.Contain("anderes aktives Gerät desselben Kunden"));}
  using(var self=await Recover(oldId.DeviceId))Assert.That(self.StatusCode,Is.EqualTo(HttpStatusCode.OK));
  using(var ok=await Recover(newId.DeviceId))Assert.That(ok.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  using(var again=await Recover(newId.DeviceId))Assert.That(WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync()),Does.Contain("bereits zur Wiederherstellung verbunden"));
  var copy=await db.ManagedJobs.AsNoTracking().SingleAsync(x=>x.DeviceId==newId.DeviceId);
  Assert.That(copy.RestoreOnly,Is.True);Assert.That(copy.SourceManagedJobId,Is.EqualTo(source.Id));
  Assert.That(await db.ManagedJobs.CountAsync(x=>x.DeviceId==foreignId.DeviceId),Is.Zero);
  // The new device receives the same destination and passphrase, but no schedule.
  var assignment=(await newPc.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"))!.Single();
  Assert.That(assignment.Definition.TargetUrl,Is.EqualTo(definition.TargetUrl));Assert.That(assignment.Definition.Passphrase,Is.EqualTo(definition.Passphrase));
  Assert.That(assignment.Definition.Schedule,Is.Null);Assert.That(assignment.Definition.RestoreOnly,Is.True);Assert.That(ConfigurationPolicy.Valid(assignment.Definition),Is.True);
  Assert.That(ConfigurationPolicy.Valid(assignment.Definition with{Schedule=definition.Schedule}),Is.False,"a recovery copy can never be scheduled");
  using(var applied=await newPc.PostAsJsonAsync($"/api/v1/agent/configurations/{copy.Id}/receipt",new ConfigurationReceipt(1,"9","Applied")))Assert.That(applied.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var backup=await db.Jobs.AsNoTracking().SingleAsync(x=>x.DeviceId==newId.DeviceId&&x.LocalId=="9");
  // A recovery copy never backs up, so it must not count as an overdue backup.
  await using(var monitor=Db()){await Monitoring.Evaluate(monitor,new(),DateTimeOffset.UtcNow);}
  Assert.That(await db.Alerts.AnyAsync(x=>x.DeviceId==newId.DeviceId&&x.Resolved==null&&(x.Code=="BackupOverdue"||x.Code=="BackupFailed")),Is.False);
  var actions=await admin.GetStringAsync($"/JobActions?id={backup.Id}");
  Assert.That(WebUtility.HtmlDecode(actions),Does.Not.Contain("name=\"step\" value=\"run\"").And.Contain("Versionen vom Gerät laden"));
  using var run=await admin.PostAsync("/JobActions?handler=Request",new FormUrlEncodedContent(new Dictionary<string,string>{{"JobId",backup.Id.ToString()},{"Action","RunBackup"},{"__RequestVerificationToken",Form(actions)}}));
  Assert.That(await db.Commands.AnyAsync(x=>x.DeviceId==newId.DeviceId&&x.Action==RemoteAction.RunBackup),Is.False,"no backup into another device's destination");
  using var list=await admin.PostAsync("/JobActions?handler=Request",new FormUrlEncodedContent(new Dictionary<string,string>{{"JobId",backup.Id.ToString()},{"Action","ListRestorePoints"},{"__RequestVerificationToken",Form(actions)}}));
  Assert.That(await db.Commands.AnyAsync(x=>x.DeviceId==newId.DeviceId&&x.Action==RemoteAction.ListRestorePoints),Is.True);
  // The recovery flag cannot be removed by editing the job.
  using var edit=await admin.PutAsJsonAsync($"/api/v1/management/managed-jobs/{copy.Id}",new ConfigurationInput(1,assignment.Definition with{RestoreOnly=null}));
  Assert.That(edit.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
  }
 }
 [Test]public async Task ReplacedDeviceHandsItsBackupsToTheNewDeviceAndExplainsMacErrors()
 {
  var(t,site)=await Customer();
  async Task<(HttpClient Client,EnrollmentResponse Identity)> Enroll(string name)
  {
   var client=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
   using var e=await client.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),name,"Darwin 25.0.0","0.3.0","osx-arm64"));var identity=(await e.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
   client.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);client.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());return (client,identity);
  }
  var (oldMac,oldId)=await Enroll("MacBook-von-Sam");var (newMac,newId)=await Enroll("MacBook-von-Sam");
  using(oldMac)using(newMac){
  using var admin=await Login(UserRole.SuperAdmin);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  var definition=new ManagedBackupDefinition("Ordner Downloads",["/Users/sam/Downloads"],"webdav://ocloud.example/dav/spaces/K1/Mac","replace-passphrase-24680",new(){{"use-ssl","true"},{"auth-username","u"},{"auth-password","p"}},30,[],new(DateTimeOffset.UtcNow,24,Enum.GetValues<DayOfWeek>()));
  using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{oldId.DeviceId}/managed-jobs",new ConfigurationInput(0,definition));
  var job=(await created.Content.ReadFromJsonAsync<ManagedJob>())!;
  using(var applied=await oldMac.PostAsJsonAsync($"/api/v1/agent/configurations/{job.Id}/receipt",new ConfigurationReceipt(1,"3","Applied")))Assert.That(applied.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  // The new device suggests the old one with the same name and explains Full Disk Access on a Mac.
  var page=WebUtility.HtmlDecode(await admin.GetStringAsync($"/Device?id={newId.DeviceId}"));
  Assert.That(page,Does.Contain("Gerät ersetzen – Backups übernehmen").And.Contain("hat denselben Namen").And.Contain("Festplattenvollzugriff").And.Not.Contain("replace-passphrase"));
  async Task<string> Replace(bool confirm)
  {
   var fields=new Dictionary<string,string>{{"oldDeviceId",oldId.DeviceId.ToString()},{"__RequestVerificationToken",Form(await admin.GetStringAsync($"/Device?id={newId.DeviceId}"))}};if(confirm)fields["confirm"]="true";
   using var response=await admin.PostAsync($"/Device?id={newId.DeviceId}&handler=Replace",new FormUrlEncodedContent(fields));Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
   return WebUtility.HtmlDecode(await admin.GetStringAsync(response.Headers.Location!.ToString()));
  }
  Assert.That(await Replace(false),Does.Contain("Bitte bestätigen"));
  await using var db=Db();
  Assert.That(await db.ManagedJobs.AsNoTracking().Where(x=>x.Id==job.Id).Select(x=>x.DeviceId).SingleAsync(),Is.EqualTo(oldId.DeviceId));
  Assert.That(await Replace(true),Does.Contain("1 Backup(s) übernommen"));
  var moved=await db.ManagedJobs.AsNoTracking().SingleAsync(x=>x.Id==job.Id);
  Assert.That(moved.DeviceId,Is.EqualTo(newId.DeviceId));Assert.That(moved.LatestRevision,Is.EqualTo(2));Assert.That(moved.LocalJobId,Is.Null);
  Assert.That(await db.Devices.AsNoTracking().Where(x=>x.Id==oldId.DeviceId).Select(x=>x.Active).SingleAsync(),Is.False);
  using(var revoked=await oldMac.GetAsync("/api/v1/agent/configurations"))Assert.That(revoked.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized),"the old device can no longer take part");
  // Until the old device confirms that its schedules are paused, the new device does not get the backups.
  Assert.That(await db.Agents.AsNoTracking().Where(x=>x.DeviceId==oldId.DeviceId).Select(x=>x.Revoked).SingleAsync(),Is.False,"revoking first would leave the old device backing up unnoticed");
  Assert.That(await newMac.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"),Is.Empty);
  Assert.That(WebUtility.HtmlDecode(await admin.GetStringAsync($"/Device?id={newId.DeviceId}")),Does.Contain("hat den Stopp noch nicht bestätigt"));
  using(var told=await oldMac.PostAsJsonAsync("/api/v1/agent/heartbeat",new HeartbeatRequest("0.3.0","Darwin 25.0.0",true,[])))Assert.That(told.StatusCode,Is.EqualTo(HttpStatusCode.Gone));
  using(var stopped=await oldMac.PostAsync("/api/v1/agent/retired",null))Assert.That(stopped.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  Assert.That(await db.Agents.AsNoTracking().Where(x=>x.DeviceId==oldId.DeviceId).Select(x=>x.Revoked).SingleAsync(),Is.True,"revoked once the stop is confirmed");
  Assert.That(await db.Devices.AsNoTracking().Where(x=>x.Id==oldId.DeviceId).Select(x=>x.RetireConfirmed).SingleAsync(),Is.Not.Null);
  Assert.That(WebUtility.HtmlDecode(await admin.GetStringAsync($"/Device?id={newId.DeviceId}")),Does.Not.Contain("hat den Stopp noch nicht bestätigt"));
  // Same destination, passphrase and schedule; the agent rebuilds its database before the first backup.
  var assignment=(await newMac.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"))!.Single();
  Assert.That(assignment.JobId,Is.EqualTo(job.Id));Assert.That(assignment.Definition.Adopted,Is.True);Assert.That(assignment.Definition.TargetUrl,Is.EqualTo(definition.TargetUrl));
  Assert.That(assignment.Definition.Passphrase,Is.EqualTo(definition.Passphrase));Assert.That(assignment.Definition.Schedule,Is.Not.Null);Assert.That(ConfigurationPolicy.Valid(assignment.Definition),Is.True);
  Assert.That(ConfigurationPolicy.Valid(assignment.Definition with{RestoreOnly=true,Schedule=null}),Is.False,"a taken-over job is never a recovery copy");
  // Editing before the agent applied the takeover keeps the marker, so the rebuild still happens.
  using(var edited=await admin.PutAsJsonAsync($"/api/v1/management/managed-jobs/{job.Id}",new ConfigurationInput(2,assignment.Definition with{Adopted=null,KeepVersions=60})))Assert.That(edited.StatusCode,Is.EqualTo(HttpStatusCode.OK));
  var latest=(await newMac.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"))!.Single();
  Assert.That(latest.Revision,Is.EqualTo(3));Assert.That(latest.Definition.Adopted,Is.True);Assert.That(latest.Definition.KeepVersions,Is.EqualTo(60));
  using(var applied=await newMac.PostAsJsonAsync($"/api/v1/agent/configurations/{job.Id}/receipt",new ConfigurationReceipt(3,"5","Applied")))Assert.That(applied.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  // A taken-over backup can still be recovered on yet another device (the recovery copy is not a takeover).
  var (spare,spareId)=await Enroll("Ersatz-Mac");using(spare){
   var customer=await db.Customers.Where(x=>x.TenantId==t.Id).Select(x=>x.Id).SingleAsync();
   using var recover=await admin.PostAsync($"/Customer?id={customer}&handler=Recover",new FormUrlEncodedContent(new Dictionary<string,string>{
    {"id",customer.ToString()},{"sourceJobId",job.Id.ToString()},{"targetDeviceId",spareId.DeviceId.ToString()},{"__RequestVerificationToken",Form(await admin.GetStringAsync($"/Customer?id={customer}"))}}));
   Assert.That(recover.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
   var copy=(await spare.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"))!.Single();
   Assert.That(copy.Definition.RestoreOnly,Is.True);Assert.That(copy.Definition.Adopted,Is.Null);
  }
  // The stored passphrase can be shown to an administrator, and every view is audited.
  var edit=await admin.GetStringAsync($"/BackupEdit?deviceId={newId.DeviceId}&id={job.Id}");
  Assert.That(edit,Does.Contain("Passphrase anzeigen").And.Contain("/Users/").And.Not.Contain("replace-passphrase"));
  using(var shown=await admin.PostAsync("/BackupEdit?handler=Passphrase",new FormUrlEncodedContent(new Dictionary<string,string>{{"DeviceId",newId.DeviceId.ToString()},{"ManagedJobId",job.Id.ToString()},{"__RequestVerificationToken",Form(edit)}})))
  {
   Assert.That(shown.Headers.CacheControl?.NoStore,Is.True);
   using var json=System.Text.Json.JsonDocument.Parse(await shown.Content.ReadAsStringAsync());Assert.That(json.RootElement.GetProperty("passphrase").GetString(),Is.EqualTo(definition.Passphrase));
  }
  Assert.That(await db.Audit.AnyAsync(x=>x.Action=="backup.passphrase-viewed"&&x.Resource==job.Id.ToString()),Is.True);
  Assert.That(WebUtility.HtmlDecode(await admin.GetStringAsync($"/BackupEdit?deviceId={newId.DeviceId}")),Does.Contain("Sicheren Schlüssel erzeugen").And.Contain("als Pfadname kopieren"));
  // A failed backup on the Mac reports a fixed code that the Console explains.
  var backup=await db.Jobs.AsNoTracking().SingleAsync(x=>x.DeviceId==newId.DeviceId&&x.LocalId=="5");
  var actions=await admin.GetStringAsync($"/JobActions?id={backup.Id}");
  using(var run=await admin.PostAsync("/JobActions?handler=Request",new FormUrlEncodedContent(new Dictionary<string,string>{{"JobId",backup.Id.ToString()},{"Action","RunBackup"},{"__RequestVerificationToken",Form(actions)}})))Assert.That((int)run.StatusCode,Is.LessThan(400));
  var command=await db.Commands.AsNoTracking().SingleAsync(x=>x.DeviceId==newId.DeviceId&&x.Action==RemoteAction.RunBackup);
  using(var bogus=await newMac.PostAsJsonAsync($"/api/v1/agent/commands/{command.Id}/receipt",new CommandReceipt("Failed",77,"Access to the path is denied")))Assert.That(bogus.StatusCode,Is.EqualTo(HttpStatusCode.BadRequest),"raw engine text is never accepted");
  using(var failed=await newMac.PostAsJsonAsync($"/api/v1/agent/commands/{command.Id}/receipt",new CommandReceipt("Failed",77,"SourceAccessDenied")))Assert.That(failed.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  Assert.That(WebUtility.HtmlDecode(await admin.GetStringAsync($"/JobActions?id={backup.Id}")),Does.Contain("Festplattenvollzugriff"));
  }
 }
 [Test]public async Task AnOfflineOldDeviceNeedsAnExplicitManualStatementAndIsStillStoppedIfItReturns()
 {
  var(t,site)=await Customer();
  async Task<(HttpClient Client,EnrollmentResponse Identity)> Enroll(string name)
  {
   var client=factory.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
   using var e=await client.PostAsJsonAsync("/api/v1/agent/enroll",new EnrollmentRequest(await Token(t,site),name,"Windows 11","0.3.0","win-x64"));var identity=(await e.Content.ReadFromJsonAsync<EnrollmentResponse>())!;
   client.DefaultRequestHeaders.Authorization=new("Bearer",identity.Credential);client.DefaultRequestHeaders.Add("X-Device-Id",identity.DeviceId.ToString());return (client,identity);
  }
  var (oldPc,oldId)=await Enroll("PC-Empfang");var (newPc,newId)=await Enroll("PC-Empfang-neu");var (spare,spareId)=await Enroll("PC-Lager");
  using(oldPc)using(newPc)using(spare){
  using var admin=await Login(UserRole.SuperAdmin);using var csrf=System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/v1/management/csrf"));admin.DefaultRequestHeaders.Add("X-CSRF-Token",csrf.RootElement.GetProperty("token").GetString());
  var definition=new ManagedBackupDefinition("Dokumente",["C:\\Users\\Empfang\\Documents"],"webdav://storage.example/dav/k1/empfang","offline-passphrase-13579",new(){{"use-ssl","true"},{"auth-username","u"},{"auth-password","p"}},30,[],new(DateTimeOffset.UtcNow.AddHours(1),24,Enum.GetValues<DayOfWeek>()));
  using var created=await admin.PostAsJsonAsync($"/api/v1/management/devices/{oldId.DeviceId}/managed-jobs",new ConfigurationInput(0,definition));var job=(await created.Content.ReadFromJsonAsync<ManagedJob>())!;
  using(var applied=await oldPc.PostAsJsonAsync($"/api/v1/agent/configurations/{job.Id}/receipt",new ConfigurationReceipt(1,"3","Applied")))Assert.That(applied.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  async Task<string> Post(Guid device,string handler,Dictionary<string,string> fields)
  {
   fields["__RequestVerificationToken"]=Form(await admin.GetStringAsync($"/Device?id={device}"));
   using var response=await admin.PostAsync($"/Device?id={device}&handler={handler}",new FormUrlEncodedContent(fields));Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
   return WebUtility.HtmlDecode(await admin.GetStringAsync(response.Headers.Location!.ToString()));
  }
  await Post(newId.DeviceId,"Replace",new(){{"oldDeviceId",oldId.DeviceId.ToString()},{"confirm","true"}});
  Assert.That(await newPc.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"),Is.Empty);
  // Without ticking the statement nothing changes; with it, the new device starts and the page says it is not a technical proof.
  Assert.That(await Post(newId.DeviceId,"ConfirmStopped",new(){{"oldDeviceId",oldId.DeviceId.ToString()}}),Does.Contain("Bitte bestätigen"));
  Assert.That(await newPc.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"),Is.Empty);
  Assert.That(await Post(newId.DeviceId,"ConfirmStopped",new(){{"oldDeviceId",oldId.DeviceId.ToString()},{"confirm","true"}}),Does.Contain("nicht technisch bestätigt"));
  Assert.That((await newPc.GetFromJsonAsync<ConfigurationAssignment[]>("/api/v1/agent/configurations"))!.Single().JobId,Is.EqualTo(job.Id));
  await using var db=Db();
  Assert.That(await db.Audit.AnyAsync(x=>x.Action=="device.retire-manually-confirmed"&&x.Resource==oldId.DeviceId.ToString()),Is.True);
  // The old PC is switched on again later: it is still told to stop.
  using(var told=await oldPc.PostAsJsonAsync("/api/v1/agent/heartbeat",new HeartbeatRequest("0.3.0","Windows 11",true,[])))Assert.That(told.StatusCode,Is.EqualTo(HttpStatusCode.Gone));
  // Decommission without replacement keeps the device's access until it confirms.
  Assert.That(await Post(spareId.DeviceId,"Decommission",new(){{"confirm","true"}}),Does.Contain("Stilllegung angefordert"));
  using(var told=await spare.PostAsJsonAsync("/api/v1/agent/heartbeat",new HeartbeatRequest("0.3.0","Windows 11",true,[])))Assert.That(told.StatusCode,Is.EqualTo(HttpStatusCode.Gone));
  Assert.That(await db.Devices.AsNoTracking().Where(x=>x.Id==spareId.DeviceId).Select(x=>x.Active).SingleAsync(),Is.True,"still listed until the stop is confirmed");
  using(var stopped=await spare.PostAsync("/api/v1/agent/retired",null))Assert.That(stopped.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
  var spareDevice=await db.Devices.AsNoTracking().SingleAsync(x=>x.Id==spareId.DeviceId);Assert.That(spareDevice.Active,Is.False);Assert.That(spareDevice.RetireConfirmed,Is.Not.Null);
  using(var again=await spare.PostAsync("/api/v1/agent/retired",null))Assert.That(again.StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
  }
 }
 [Test]public async Task GuidedRestoreAndMonthlyCheckUseSignedCommands()
 {
  var(t,site)=await Customer();await using var db=Db();var now=DateTimeOffset.UtcNow;
  var device=new Device{TenantId=t.Id,SiteId=site.Id,Name="Restore worker",EngineReachable=true,LastHeartbeat=now};db.Devices.Add(device);
  var job=new BackupJob{TenantId=t.Id,DeviceId=device.Id,LocalId="4",Name="Dokumente"};db.Jobs.Add(job);
  db.Runs.Add(new BackupRun{TenantId=t.Id,JobId=job.Id,LocalRunId="r1",Started=now.AddHours(-2),Completed=now.AddHours(-1),Status=RunStatus.Success});await db.SaveChangesAsync();
  var signer=factory.Services.GetRequiredService<CommandSigning>();var store=factory.Services.GetRequiredService<ISecretStore>();
  // Monthly check: queued once for an online device with a successful backup, then not again the same day.
  await using(var scoped=Db()){Assert.That(await CommandApi.QueueDueVerifications(scoped,signer,store,now,30),Is.GreaterThanOrEqualTo(1));}
  var check=await db.Commands.AsNoTracking().SingleAsync(x=>x.JobId==job.Id&&x.Action==RemoteAction.VerifyBackup);
  Assert.That(check.RequestedBy,Is.EqualTo(CommandApi.Scheduler));Assert.That(check.Status,Is.EqualTo("Pending"));
  await using(var scoped=Db()){await CommandApi.QueueDueVerifications(scoped,signer,store,now.AddHours(2),30);}
  Assert.That(await db.Commands.CountAsync(x=>x.JobId==job.Id&&x.Action==RemoteAction.VerifyBackup),Is.EqualTo(1));
  using var admin=await Login(UserRole.SuperAdmin);
  string Form(string html){var m=Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");Assert.That(m.Success,Is.True);return WebUtility.HtmlDecode(m.Groups[1].Value);}
  async Task<HttpResponseMessage> Guide(Dictionary<string,string> values,string query="")
  {var page=await admin.GetStringAsync($"/JobActions?id={job.Id}{query}");values["JobId"]=job.Id.ToString();values["__RequestVerificationToken"]=Form(page);return await admin.PostAsync("/JobActions?handler=Guide",new FormUrlEncodedContent(values));}
  async Task Answer(RemoteAction action,RestoreCatalog catalog)
  {
   var c=await db.Commands.Where(x=>x.JobId==job.Id&&x.Action==action).OrderByDescending(x=>x.Expires).FirstAsync();
   c.Status="Completed";c.EncryptedCatalog=store.Protect(c.TenantId,$"catalog:{c.Id}",System.Text.Json.JsonSerializer.Serialize(catalog));await db.SaveChangesAsync();
  }
  var page=await admin.GetStringAsync($"/JobActions?id={job.Id}");Assert.That(WebUtility.HtmlDecode(page),Does.Contain("Versionen vom Gerät laden").And.Contain("Monatliche Prüfung (automatisch)"));
  using(var r=await Guide(new(){{"step","versions"}}))Assert.That(r.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  Assert.That(WebUtility.HtmlDecode(await admin.GetStringAsync($"/JobActions?id={job.Id}")),Does.Contain("Warte auf Antwort des Geräts"));
  var version=new DateTimeOffset(2026,10,8,20,15,0,TimeSpan.Zero);
  await Answer(RemoteAction.ListRestorePoints,new([new(version,12,4096)],[],false));
  page=WebUtility.HtmlDecode(await admin.GetStringAsync($"/JobActions?id={job.Id}"));Assert.That(page,Does.Contain(version.ToString("O")).And.Contain("12 Dateien"));
  using(var r=await Guide(new(){{"step","files"},{"point",version.ToString("O")}}))Assert.That(r.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  await Answer(RemoteAction.ListRestoreFiles,new([],[new("C:\\Daten\\",null,true),new("C:\\Daten\\Rechnung.pdf",2048,false)],false));
  var query=$"&point={Uri.EscapeDataString(version.ToString("O"))}";
  page=WebUtility.HtmlDecode(await admin.GetStringAsync($"/JobActions?id={job.Id}{query}"));
  Assert.That(page,Does.Contain("value=\"C:\\Daten\\Rechnung.pdf\"").And.Contain("Wohin wiederherstellen?"));
  using(var bad=await Guide(new(){{"step","restore"},{"point",version.ToString("O")},{"paths","C:\\Daten\\Rechnung.pdf"},{"target","folder"},{"folder","../etc"}},query))
   Assert.That(WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()),Does.Contain("Der Ordnername darf nur"));
  using(var none=await Guide(new(){{"step","restore"},{"point",version.ToString("O")},{"target","original"}},query))
   Assert.That(WebUtility.HtmlDecode(await none.Content.ReadAsStringAsync()),Does.Contain("mindestens eine Datei"));
  using(var ok=await Guide(new(){{"step","restore"},{"point",version.ToString("O")},{"paths","C:\\Daten\\Rechnung.pdf"},{"target","original"}},query))Assert.That(ok.StatusCode,Is.EqualTo(HttpStatusCode.Redirect));
  var restore=await db.Commands.AsNoTracking().SingleAsync(x=>x.JobId==job.Id&&x.Action==RemoteAction.Restore);
  var payload=System.Text.Json.JsonSerializer.Deserialize<DeviceCommand>(Convert.FromBase64String(store.Unprotect(restore.TenantId,$"command:{restore.Id}",restore.EncryptedPayload)))!;
  Assert.That(payload.Restore!.OriginalLocation,Is.True);Assert.That(payload.Restore.DestinationFolder,Is.Empty);Assert.That(payload.Restore.Paths,Is.EqualTo(new[]{"C:\\Daten\\Rechnung.pdf"}));
  Assert.That(payload.Restore.Snapshot,Is.EqualTo(version));Assert.That(CommandProtocol.Valid(payload,device.Id,DateTimeOffset.UtcNow),Is.True);
  await using(var scoped=Db())Assert.That(restore.Status,Is.EqualTo(await CommandApi.SecondApprovalPossible(scoped)?"AwaitingApproval":"Pending"));
 }
 private sealed class TestFactory(string cs,string dir):WebApplicationFactory<Program>
 {
  protected override void ConfigureWebHost(IWebHostBuilder b)
  {
   b.UseEnvironment("Development");b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:Management",cs},{"Security:MasterKeyFile",Path.Combine(dir,"master.key")},{"Security:KeyDirectory",Path.Combine(dir,"keys")},{"Monitoring:PollSeconds","3600"},{"Commands:SigningKeyFile",Path.Combine(dir,"commands.pem")},{"Updates:PublicKeyFile",Path.Combine(dir,"commands.pem")},{"Agents:ApiBase","http://127.0.0.1:9"},{"Verifications:Enabled","false"}}));
  }
 }
}
