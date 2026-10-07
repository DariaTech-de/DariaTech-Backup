using System.Net;
using System.Net.Mail;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Services;

public sealed class NotificationOptions
{
 public bool Enabled {get;set;} public string Host {get;set;}=""; public int Port {get;set;}=587;
 public string Sender {get;set;}=""; public string Username {get;set;}=""; public string? PasswordFile {get;set;}
 public int PollSeconds {get;set;}=30;
 public bool Valid()=>!Enabled||!string.IsNullOrWhiteSpace(Host)&&!Host.Any(char.IsControl)&&Port is >0 and <=65535&&ValidAddress(Sender)&&(string.IsNullOrEmpty(Username)||!string.IsNullOrEmpty(PasswordFile)&&File.Exists(PasswordFile));
 public static bool ValidAddress(string? address)=>address is not null&&address.Length<=254&&!address.Any(char.IsControl)&&MailAddress.TryCreate(address,out var parsed)&&parsed.Address==address;
}
public sealed record NotificationMessage(string Recipient,string Subject,string Body,string MessageId);
public interface INotificationTransport {Task Send(NotificationMessage message,CancellationToken ct);}
public sealed class SmtpTransport(IOptions<NotificationOptions> settings,ISecretStore secrets):INotificationTransport
{
 public async Task Send(NotificationMessage message,CancellationToken ct)
 {
  var o=settings.Value;if(!o.Enabled||!o.Valid())throw new InvalidOperationException("SMTP transport is not configured");
  using var smtp=new SmtpClient(o.Host,o.Port){EnableSsl=true,Timeout=30000,UseDefaultCredentials=false};
  if(!string.IsNullOrEmpty(o.Username))smtp.Credentials=new NetworkCredential(o.Username,secrets.Unprotect(Guid.Empty,"smtp-password",File.ReadAllText(o.PasswordFile!).Trim()));
  using var mail=new MailMessage(o.Sender,message.Recipient,message.Subject,message.Body){IsBodyHtml=false};
  mail.Headers["Message-ID"]=$"<{message.MessageId}@dariatech-backup.invalid>";
  // Platform certificate validation remains enabled; no fallback to cleartext SMTP.
  await smtp.SendMailAsync(mail,ct);
 }
}
public sealed class Notifications(IServiceScopeFactory scopes,IOptions<NotificationOptions> options,INotificationTransport transport,ILogger<Notifications> log):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken ct)
 {
  if(!options.Value.Enabled)return;
  using var timer=new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds,10,3600)));
  do
  {
   try
   {
    using var scope=scopes.CreateScope();scope.ServiceProvider.GetRequiredService<TenantScope>().Maintenance=true;
    var db=scope.ServiceProvider.GetRequiredService<ManagementDb>();await Plan(db,DateTimeOffset.UtcNow,ct);
    for(var i=0;i<20;i++)if(!await DeliverOne(db,transport,DateTimeOffset.UtcNow,ct))break;
   }
   catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
   catch(Exception e){log.LogError("Notification cycle failed ({Type})",e.GetType().Name);}
  }while(await timer.WaitForNextTickAsync(ct));
 }
 public static async Task Plan(ManagementDb db,DateTimeOffset now,CancellationToken ct=default)
 {
  await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({73941004L})",ct);
  var rules=await db.NotificationRules.Where(x=>x.Enabled&&x.Channel=="Email").ToListAsync(ct);
  var active=await db.Customers.Where(x=>x.Active).Select(x=>x.TenantId).ToArrayAsync(ct);
  var alerts=await db.Alerts.ToListAsync(ct);
  var deliveries=await db.Deliveries.ToListAsync(ct);
  foreach(var pending in deliveries.Where(x=>x.Status is "Pending" or "Retry"))
  {
   if(!active.Contains(pending.TenantId)||rules.All(x=>x.Id!=pending.RuleId))pending.Status="Cancelled";
   else if(pending.Kind=="Alert"&&alerts.Any(x=>x.Id==pending.AlertId&&(x.Resolved is not null||x.Opened!=pending.Occurrence)))pending.Status="Cancelled";
  }
  foreach(var rule in rules.Where(x=>active.Contains(x.TenantId)))
  foreach(var alert in alerts.Where(x=>x.TenantId==rule.TenantId))
  {
   var occurrence=deliveries.Where(x=>x.RuleId==rule.Id&&x.AlertId==alert.Id&&x.Occurrence==alert.Opened).ToArray();
   if(alert.Resolved is not null)
   {
    if(occurrence.Any(x=>x.Kind=="Alert"&&x.Sent is not null)&&occurrence.All(x=>x.Kind!="Recovery"))Add("Recovery",0);
    continue;
   }
   var previous=occurrence.Where(x=>x.Kind=="Alert").OrderByDescending(x=>x.Sequence).FirstOrDefault();
   if(previous is null)Add("Alert",0);
   else if(previous.Status is "Sent" or "DeadLetter" && (previous.Sent??previous.Created).AddMinutes(Math.Clamp(rule.RepeatMinutes,60,43200))<=now)Add("Alert",previous.Sequence+1);
   void Add(string kind,int sequence)
   {
    var delivery=new NotificationDelivery{TenantId=alert.TenantId,RuleId=rule.Id,AlertId=alert.Id,Occurrence=alert.Opened,Kind=kind,Sequence=sequence,Created=now,Due=now};db.Deliveries.Add(delivery);deliveries.Add(delivery);
   }
  }
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
 }
 public static async Task<bool> DeliverOne(ManagementDb db,INotificationTransport transport,DateTimeOffset now,CancellationToken ct=default)
 {
  var id=await db.Deliveries.AsNoTracking().Where(x=>((x.Status=="Pending"||x.Status=="Retry")&&x.Due<=now)||(x.Status=="Sending"&&x.LeaseUntil<now)).OrderBy(x=>x.Due).Select(x=>(Guid?)x.Id).FirstOrDefaultAsync(ct);
  if(id is null)return false;
  var claimed=await db.Deliveries.Where(x=>x.Id==id&&(((x.Status=="Pending"||x.Status=="Retry")&&x.Due<=now)||(x.Status=="Sending"&&x.LeaseUntil<now)))
   .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Sending").SetProperty(x=>x.LeaseUntil,now.AddMinutes(2)).SetProperty(x=>x.Attempts,x=>x.Attempts+1),ct);
  if(claimed!=1)return true;
  var delivery=await db.Deliveries.SingleAsync(x=>x.Id==id,ct);await db.Entry(delivery).ReloadAsync(ct);
  var rule=await db.NotificationRules.SingleAsync(x=>x.Id==delivery.RuleId,ct);var alert=await db.Alerts.SingleAsync(x=>x.Id==delivery.AlertId,ct);
  await db.Entry(rule).ReloadAsync(ct);await db.Entry(alert).ReloadAsync(ct);
  if(!rule.Enabled||!NotificationOptions.ValidAddress(rule.Recipient)||!await db.Customers.AnyAsync(x=>x.TenantId==delivery.TenantId&&x.Active,ct)||delivery.Kind=="Alert"&&(alert.Resolved is not null||alert.Opened!=delivery.Occurrence))delivery.Status="Cancelled";
  else
  {
   var device=await db.Devices.SingleAsync(x=>x.Id==alert.DeviceId,ct);var customer=await db.Customers.SingleAsync(x=>x.TenantId==alert.TenantId,ct);
   var message=new NotificationMessage(rule.Recipient,$"DariaTech Backup: {(delivery.Kind=="Recovery"?"Entwarnung":"Backup-Warnung")}",
    $"Kunde: {customer.Name}\nGerät: {device.Name}\nStatus: {(delivery.Kind=="Recovery"?"Behoben":alert.Severity)}\nMeldung: {alert.Code}\nZeitpunkt (UTC): {delivery.Occurrence:O}\nWeitere Details finden Sie in Ihrer DariaTech Backup Console.",delivery.Id.ToString("N"));
   try
   {
    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));await transport.Send(message,timeout.Token);
    delivery.Status="Sent";delivery.Sent=DateTimeOffset.UtcNow;delivery.ErrorCode=null;
   }
   catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
   catch(Exception){delivery.Status=delivery.Attempts>=8?"DeadLetter":"Retry";delivery.ErrorCode="DeliveryFailed";delivery.Due=now.AddMinutes(Math.Min(240,5*Math.Pow(2,delivery.Attempts-1)));}
  }
  delivery.LeaseUntil=null;await db.SaveChangesAsync(ct);return true;
 }
}
