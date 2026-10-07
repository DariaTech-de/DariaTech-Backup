using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Data;

public sealed class ManagementDb(DbContextOptions<ManagementDb> options, TenantScope scope) : DbContext(options)
{
 public DbSet<Tenant> Tenants => Set<Tenant>(); public DbSet<Customer> Customers => Set<Customer>();
 public DbSet<Site> Sites => Set<Site>(); public DbSet<Device> Devices => Set<Device>();
 public DbSet<Agent> Agents => Set<Agent>(); public DbSet<BackupJob> Jobs => Set<BackupJob>();
 public DbSet<BackupRun> Runs => Set<BackupRun>(); public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
 public DbSet<User> Users => Set<User>(); public DbSet<Role> Roles => Set<Role>();
 public DbSet<Alert> Alerts => Set<Alert>(); public DbSet<AuditEvent> Audit => Set<AuditEvent>();
 public DbSet<StorageTarget> StorageTargets => Set<StorageTarget>();
 public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
 public DbSet<ManagedJob> ManagedJobs => Set<ManagedJob>();
 public DbSet<ConfigurationRevision> ConfigurationRevisions => Set<ConfigurationRevision>();
 public DbSet<RemoteCommand> Commands => Set<RemoteCommand>();
 protected override void OnModelCreating(ModelBuilder b)
 {
  b.Entity<Tenant>().HasQueryFilter(x => scope.Global || x.Id == scope.TenantId);
  foreach (var type in typeof(TenantEntity).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(TenantEntity).IsAssignableFrom(t)))
  {
   var e = b.Entity(type); e.HasBaseType((Type?)null);
   e.HasKey("Id"); e.HasAlternateKey("TenantId", "Id");
   e.HasOne(typeof(Tenant)).WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict);
   var p = Expression.Parameter(type, "x");
   Expression<Func<bool>> global = () => scope.Global;
   Expression<Func<Guid?>> tenant = () => scope.TenantId;
   e.HasQueryFilter(Expression.Lambda(Expression.OrElse(global.Body,
    Expression.Equal(Expression.Convert(Expression.Property(p,"TenantId"),typeof(Guid?)),tenant.Body)),p));
  }
  b.Entity<Customer>().HasIndex(x => x.TenantId).IsUnique();
  b.Entity<Customer>().HasIndex(x => x.Number).IsUnique();
  b.Entity<Site>().HasIndex(x => new {x.TenantId, x.Name}).IsUnique();
  b.Entity<Device>().HasOne<Site>().WithMany().HasForeignKey(x => new {x.TenantId,x.SiteId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Agent>().HasOne<Device>().WithMany().HasForeignKey(x=>new{x.TenantId,x.DeviceId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Agent>().HasIndex(x=>x.DeviceId).IsUnique();
  b.Entity<Agent>().HasIndex(x=>x.CredentialHash).IsUnique();
  b.Entity<BackupJob>().HasOne<Device>().WithMany().HasForeignKey(x=>new{x.TenantId,x.DeviceId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<BackupJob>().HasIndex(x=>new{x.DeviceId,x.LocalId}).IsUnique();
  b.Entity<BackupRun>().HasOne<BackupJob>().WithMany().HasForeignKey(x=>new{x.TenantId,x.JobId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<BackupRun>().HasIndex(x=>new{x.JobId,x.LocalRunId}).IsUnique();
  b.Entity<EnrollmentToken>().HasOne<Site>().WithMany().HasForeignKey(x=>new{x.TenantId,x.SiteId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<EnrollmentToken>().HasIndex(x=>x.TokenHash).IsUnique();
  b.Entity<Alert>().HasOne<Device>().WithMany().HasForeignKey(x=>new{x.TenantId,x.DeviceId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Alert>().HasIndex(x=>new{x.DeviceId,x.Key}).IsUnique();
  b.Entity<ManagedJob>().HasOne<Device>().WithMany().HasForeignKey(x=>new{x.TenantId,x.DeviceId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<ConfigurationRevision>().HasOne<ManagedJob>().WithMany().HasForeignKey(x=>new{x.TenantId,x.ManagedJobId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<ConfigurationRevision>().HasIndex(x=>new{x.ManagedJobId,x.Revision}).IsUnique();
  b.Entity<RemoteCommand>().HasOne<Device>().WithMany().HasForeignKey(x=>new{x.TenantId,x.DeviceId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<RemoteCommand>().HasOne<BackupJob>().WithMany().HasForeignKey(x=>new{x.TenantId,x.JobId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
  b.Entity<RemoteCommand>().HasIndex(x=>new{x.DeviceId,x.Status,x.Expires});
  b.Entity<Role>().HasData(Enum.GetValues<UserRole>().Select(x=>new Role{Id=x,Name=x.ToString()}));
  b.Entity<User>().HasIndex(x=>x.Email).IsUnique();
  b.Entity<User>().Property(x=>x.LastTotpStep).IsConcurrencyToken();
  b.Entity<User>().HasOne<Tenant>().WithMany().HasForeignKey(x=>x.TenantId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<User>().HasOne<Role>().WithMany().HasForeignKey(x=>x.Role).OnDelete(DeleteBehavior.Restrict);
  b.Entity<User>().ToTable(t=>t.HasCheckConstraint("user_role_scope", """("Role" IN (4,5) AND "TenantId" IS NOT NULL) OR ("Role" IN (0,1,2,3) AND "TenantId" IS NULL)"""));
  b.Entity<AuditEvent>().HasOne<Tenant>().WithMany().HasForeignKey(x=>x.TenantId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<BackupRun>().ToTable(t=>t.HasCheckConstraint("backup_run_statistics", """
   "Bytes" >= 0 AND "Files" >= 0 AND "StorageBytes" >= 0 AND ("Completed" IS NULL OR "Completed" >= "Started")
   """));
  b.Entity<AuditEvent>().HasQueryFilter(x=>scope.Global || (scope.TenantId != null && x.TenantId == scope.TenantId));
  foreach(var entity in b.Model.GetEntityTypes())
   foreach(var property in entity.GetProperties().Where(p=>p.ClrType==typeof(string))) property.SetMaxLength(property.Name is "EncryptedConfiguration" or "EncryptedPayload" ? 120000 : property.Name.Contains("Secret") || property.Name.Contains("Credential") ? 4096 : 1000);
 }
 public override int SaveChanges(bool acceptAllChangesOnSuccess) {ValidateScope();return base.SaveChanges(acceptAllChangesOnSuccess);}
 public override Task<int> SaveChangesAsync(CancellationToken ct=default)
 {ValidateScope();return base.SaveChangesAsync(ct);}
 private void ValidateScope()
 {
  if(ChangeTracker.Entries<ConfigurationRevision>().Any(e=>e.State is EntityState.Modified or EntityState.Deleted))throw new InvalidOperationException("Configuration revisions are immutable");
  foreach(var e in ChangeTracker.Entries<ITenantEntity>().Where(e=>e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
  {
   if(!scope.Allows(e.Entity.TenantId)) throw new UnauthorizedAccessException("Tenant scope denied");
   if(e.State==EntityState.Modified && e.Property("TenantId").IsModified) throw new InvalidOperationException("Tenant ownership is immutable");
  }
  foreach(var e in ChangeTracker.Entries<Tenant>().Where(e=>e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
   if(!scope.Allows(e.Entity.Id))throw new UnauthorizedAccessException("Tenant scope denied");
  foreach(var e in ChangeTracker.Entries<AuditEvent>())
   if(e.State is EntityState.Modified or EntityState.Deleted)throw new InvalidOperationException("Audit events are append-only");
 }
}
