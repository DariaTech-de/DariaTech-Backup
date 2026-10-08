using DariaTech.Console.Data;
using DariaTech.Console.Security;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Services;
public static class Privacy
{
 public static async Task EraseCustomer(ManagementDb db,Guid tenant,bool apply,CancellationToken ct=default)
 {
  var customer=await db.Customers.SingleOrDefaultAsync(x=>x.TenantId==tenant,ct)??throw new InvalidOperationException("Customer not found");
  if(customer.Active||await db.Devices.AnyAsync(x=>x.TenantId==tenant&&x.Active,ct)||await db.Agents.AnyAsync(x=>x.TenantId==tenant&&!x.Revoked,ct))throw new InvalidOperationException("Archive customer and revoke all agents before erasure");
  if(!apply)return;
  await using var tx=await db.Database.BeginTransactionAsync(ct);
  await RequireOwner(db,ct);
  db.Audit.Add(new AuditEvent{TenantId=tenant,Actor="local-privacy-maintenance",Action="tenant.erasure-started",Resource=tenant.ToString()});await db.SaveChangesAsync(ct);
  await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('dariatech.erase_tenant',{tenant.ToString()},true)",ct);
  await db.Deliveries.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.NotificationRules.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.Alerts.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.Commands.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.UpdateDeployments.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.ConfigurationRevisions.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.ManagedJobs.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.Runs.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.Jobs.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.EnrollmentTokens.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.Agents.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.Devices.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.Sites.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  await db.StorageTargets.Where(x=>x.TenantId==tenant).ExecuteDeleteAsync(ct);
  foreach(var user in await db.Users.Where(x=>x.TenantId==tenant).ToListAsync(ct))
  {user.Active=false;user.Email=$"erased-{user.Id:N}@invalid.example";user.PasswordHash="";user.TotpSecret="";user.SessionVersion=Guid.NewGuid();user.LockedUntil=null;user.FailedLogins=0;}
  db.Customers.Remove(customer);var root=await db.Tenants.SingleAsync(x=>x.Id==tenant,ct);root.Name=$"Erased tenant {tenant:N}";
  db.Audit.Add(new AuditEvent{TenantId=tenant,Actor="local-privacy-maintenance",Action="tenant.erasure-completed",Resource=tenant.ToString()});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
 }
 public static async Task<(int Runs,int Deliveries,int Alerts,int Tokens,int Commands)> Prune(ManagementDb db,int days,bool apply,CancellationToken ct=default)
 {
  if(days is <30 or >3650)throw new InvalidOperationException("Retention must be 30–3650 days");
  var cutoff=DateTimeOffset.UtcNow.AddDays(-days);
  var runs=db.Runs.Where(x=>(x.Completed!=null&&x.Completed<cutoff)||(x.Completed==null&&x.Status!=DariaTech.Contracts.RunStatus.Running&&x.Started<cutoff));
  var deliveries=db.Deliveries.Where(x=>x.Created<cutoff&&(x.Status=="Sent"||x.Status=="Cancelled"||x.Status=="DeadLetter"));
  var tokens=db.EnrollmentTokens.Where(x=>x.Expires<cutoff);
  var commands=db.Commands.Where(x=>x.Expires<cutoff&&(x.Status=="Completed"||x.Status=="Failed"||x.Status=="Rejected"||x.Status=="Indeterminate"||x.Status=="Pending"||x.Status=="AwaitingApproval"));
  var alerts=db.Alerts.Where(x=>x.Resolved<cutoff&&!db.Deliveries.Any(d=>d.AlertId==x.Id&&!(d.Created<cutoff&&(d.Status=="Sent"||d.Status=="Cancelled"||d.Status=="DeadLetter"))));
  var counts=(Runs:await runs.CountAsync(ct),Deliveries:await deliveries.CountAsync(ct),Alerts:await alerts.CountAsync(ct),Tokens:await tokens.CountAsync(ct),Commands:await commands.CountAsync(ct));
  if(!apply)return counts;
  await using var tx=await db.Database.BeginTransactionAsync(ct);await RequireOwner(db,ct);
  counts.Runs=await runs.ExecuteDeleteAsync(ct);counts.Deliveries=await deliveries.ExecuteDeleteAsync(ct);counts.Tokens=await tokens.ExecuteDeleteAsync(ct);counts.Commands=await commands.ExecuteDeleteAsync(ct);
  counts.Alerts=await db.Alerts.Where(x=>x.Resolved<cutoff&&!db.Deliveries.Any(d=>d.AlertId==x.Id)).ExecuteDeleteAsync(ct);
  db.Audit.Add(new AuditEvent{Actor="local-privacy-maintenance",Action="retention.pruned",Resource=$"runs={counts.Runs};deliveries={counts.Deliveries};alerts={counts.Alerts};tokens={counts.Tokens};commands={counts.Commands}"});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return counts;
 }
 private static async Task RequireOwner(ManagementDb db,CancellationToken ct)
 {
  var owner=await db.Database.SqlQueryRaw<bool>("""
   SELECT current_user = pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid='public."ConfigurationRevisions"'::regclass)) AS "Value"
   """).SingleAsync(ct);
  if(!owner)throw new UnauthorizedAccessException("Privacy maintenance requires the migration/table-owner connection");
 }
}
