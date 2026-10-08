using DariaTech.Contracts;
namespace DariaTech.Console.Data;

public interface ITenantEntity { Guid TenantId { get; set; } }
public abstract class TenantEntity : ITenantEntity
{
 public Guid Id { get; set; } = Guid.NewGuid();
 public Guid TenantId { get; set; }
}
public sealed class Tenant { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = ""; }
public sealed class Customer : TenantEntity
{
 public string Name { get; set; } = ""; public string Number { get; set; } = "";
 public string Contact { get; set; } = ""; public string Email { get; set; } = "";
 public string Phone { get; set; } = ""; public string Address { get; set; } = "";
 public string Notes { get; set; } = ""; public bool Active { get; set; } = true;
}
public sealed class Site : TenantEntity { public string Name { get; set; } = ""; public string Address { get; set; } = ""; }
public sealed class Device : TenantEntity
{
 public Guid SiteId { get; set; } public string Name { get; set; } = "";
 public string Hostname { get; set; } = ""; public string OperatingSystem { get; set; } = "";
 public DateTimeOffset? LastHeartbeat { get; set; } public bool EngineReachable { get; set; }
 public bool Active { get; set; } = true;
 public string? ActiveJobLocalId { get; set; } public double? Progress { get; set; }
 public long? ActiveTaskId { get; set; } public long ProgressBytes { get; set; } public long ProgressFiles { get; set; }
}
public sealed class Agent : TenantEntity
{
 public Guid DeviceId { get; set; } public string Version { get; set; } = "";
 public string CredentialHash { get; set; } = ""; public bool Revoked { get; set; }
 public DateTimeOffset Registered { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class BackupJob : TenantEntity
{
 public Guid DeviceId { get; set; } public string LocalId { get; set; } = "";
 public string Name { get; set; } = ""; public DateTimeOffset? NextRun { get; set; }
 public string Ownership { get; set; } = "Local"; public bool Active { get; set; } = true;
}
public sealed class BackupRun : TenantEntity
{
 public Guid JobId { get; set; } public string LocalRunId { get; set; } = "";
 public DateTimeOffset Started { get; set; } public DateTimeOffset? Completed { get; set; }
 public RunStatus Status { get; set; } public long? Bytes { get; set; } public long? Files { get; set; }
 public long? StorageBytes { get; set; } public double? Progress { get; set; }
 public string? ErrorCode { get; set; }
}
public sealed class EnrollmentToken : TenantEntity
{
 public Guid SiteId { get; set; } public string TokenHash { get; set; } = "";
 public DateTimeOffset Expires { get; set; } public DateTimeOffset? Used { get; set; }
}
public enum UserRole { SuperAdmin, Administrator, Technician, ReadOnly, CustomerAdmin, CustomerReadOnly }
public sealed class Role { public UserRole Id { get; set; } public string Name { get; set; } = ""; }
public sealed class User
{
 public Guid Id { get; set; } = Guid.NewGuid(); public string Email { get; set; } = "";
 public Guid? TenantId { get; set; } public UserRole Role { get; set; }
 public string PasswordHash { get; set; } = ""; public string TotpSecret { get; set; } = "";
 public long LastTotpStep { get; set; } = -1; public int FailedLogins { get; set; }
 public DateTimeOffset? LockedUntil { get; set; } public bool Active { get; set; } = true;
 public Guid SessionVersion { get; set; } = Guid.NewGuid();
}
public sealed class Alert : TenantEntity
{
 public Guid DeviceId { get; set; } public string Key { get; set; } = "";
 public string Severity { get; set; } = "Warning"; public string Code { get; set; } = "";
 public DateTimeOffset Opened { get; set; } = DateTimeOffset.UtcNow;
 public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
 public DateTimeOffset? Resolved { get; set; }
}
public sealed class AuditEvent
{
 public long Id { get; set; } public Guid? TenantId { get; set; }
 public string Actor { get; set; } = ""; public string Action { get; set; } = "";
 public string Resource { get; set; } = ""; public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class StorageTarget : TenantEntity
{
 public string Name { get; set; } = ""; public string Backend { get; set; } = "";
 public string EncryptedCredential { get; set; } = "";
}
public sealed class NotificationRule : TenantEntity
{
 public string Channel { get; set; } = "Email"; public string Recipient { get; set; } = "";
 public int RepeatMinutes { get; set; } = 1440; public bool Enabled { get; set; }
}

public sealed class ManagedJob : TenantEntity
{
 public Guid DeviceId { get; set; } public string Name { get; set; } = "";
 public long LatestRevision { get; set; } public long AppliedRevision { get; set; } public long LastReportedRevision { get; set; }
 public string? LocalJobId { get; set; } public string Status { get; set; } = "Pending";
}
public sealed class ConfigurationRevision : TenantEntity
{
 public Guid ManagedJobId { get; set; } public long Revision { get; set; }
 public string EncryptedConfiguration { get; set; } = "";
 public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RemoteCommand : TenantEntity
{
 public Guid DeviceId { get; set; } public Guid JobId { get; set; }
 public string RequestedBy { get; set; } = ""; public string? ApprovedBy { get; set; }
 public string EncryptedPayload { get; set; } = ""; public string Signature { get; set; } = ""; public string? EncryptedCatalog {get;set;}
 public RemoteAction Action { get; set; } public DateTimeOffset Expires { get; set; }
 public string Status { get; set; } = "Pending"; public long? TaskId { get; set; }
 public string? ErrorCode { get; set; }
}

public sealed class NotificationDelivery : TenantEntity
{
 public Guid RuleId {get;set;} public Guid AlertId {get;set;}
 public DateTimeOffset Occurrence {get;set;} public int Sequence {get;set;}
 public string Kind {get;set;}="Alert"; public string Status {get;set;}="Pending";
 public DateTimeOffset Created {get;set;}=DateTimeOffset.UtcNow;
 public DateTimeOffset Due {get;set;}=DateTimeOffset.UtcNow;
 public DateTimeOffset? LeaseUntil {get;set;} public DateTimeOffset? Sent {get;set;}
 public int Attempts {get;set;} public string? ErrorCode {get;set;}
}

public sealed class ApprovedAgentRelease
{
 public Guid Id {get;set;} public long Sequence {get;set;} public string Version {get;set;}="";
 public string Payload {get;set;}=""; public string Signature {get;set;}="";
 public DateTimeOffset Expires {get;set;}
}
public sealed class UpdateDeployment : TenantEntity
{
 public Guid DeviceId {get;set;} public Guid ReleaseId {get;set;}
 public string Status {get;set;}="Approved"; public string? ErrorCode {get;set;}
 public DateTimeOffset Approved {get;set;}=DateTimeOffset.UtcNow;
}
