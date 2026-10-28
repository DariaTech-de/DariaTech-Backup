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
 // Number of devices the contract covers; null: the devices registered so far.
 public int? ExpectedDevices { get; set; }
 // Contract end. Each step is recorded on its own; none implies another. Archives at the storage are never
 // deleted by this software: ArchiveDeletionApproved only records the approval for a manual deletion.
 public DateOnly? ContractEnd { get; set; } public DateOnly? RetainArchivesUntil { get; set; } public string HandoverNote { get; set; } = "";
 public DateTimeOffset? AccessLocked { get; set; } public DateTimeOffset? StorageAccessRevoked { get; set; } public string? StorageAccessRevokedBy { get; set; }
 public DateTimeOffset? ArchiveDeletionApproved { get; set; } public string? ArchiveDeletionApprovedBy { get; set; } public DateTimeOffset? Closed { get; set; }
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
 // Set while the device's job list arrives incomplete (invalid or duplicate entries had to be dropped).
 public DateTimeOffset? InventoryIncompleteSince { get; set; }
 // Retirement (replaced or decommissioned): requested by an administrator; confirmed by the agent once it has
 // paused its scheduled backups, or - for a device that will never come back - confirmed manually by an
 // administrator (no technical proof). Until the agent confirms, its credential stays valid so it can still
 // learn about the retirement the next time it is online.
 public DateTimeOffset? RetireRequested { get; set; } public DateTimeOffset? RetireConfirmed { get; set; }
 public DateTimeOffset? RetireManuallyConfirmed { get; set; } public string? RetireManuallyConfirmedBy { get; set; }
 public Guid? ReplacedByDeviceId { get; set; }
 // Deleted by an administrator (not replaced): left out of all lists at once; its backups stay restorable.
 public DateTimeOffset? Deleted { get; set; } public string? DeletedBy { get; set; }
 // Planned absence (holiday, device in storage): offline and overdue are not alerted until this time; failures
 // still are. After it ends normal monitoring applies again without any further action.
 public DateTimeOffset? AbsentUntil { get; set; } public string AbsenceNote { get; set; } = "";
 public bool RetirementPending=>RetireRequested is not null&&RetireConfirmed is null&&RetireManuallyConfirmed is null;
}
public sealed class Agent : TenantEntity
{
 public Guid DeviceId { get; set; } public string Version { get; set; } = ""; public string Platform {get;set;}="";
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
 public long? QuotaFreeBytes {get;set;} public long? QuotaTotalBytes {get;set;}
 public bool? QuotaWarning {get;set;} public bool? QuotaError {get;set;} public bool? RetentionError {get;set;}
}
public sealed class EnrollmentToken : TenantEntity
{
 public Guid SiteId { get; set; } public string TokenHash { get; set; } = "";
 public DateTimeOffset Expires { get; set; } public DateTimeOffset? Used { get; set; }
 public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow; public Guid? DeviceId { get; set; }
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
 // Work state: someone took the alert over ("In Bearbeitung"). It is resolved only by monitoring, when the
 // problem is no longer detected ("behoben" is technical evidence); a new occurrence starts unassigned.
 public string? AssignedTo { get; set; } public DateTimeOffset? InProgressSince { get; set; }
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
 // Set when the device confirms applying a revision, cleared once a heartbeat lists the job. Until then a heartbeat
 // may carry a job list taken before the job existed.
 public DateTimeOffset? UnreportedSince { get; set; }
 // Recovery copy of another device's job: same destination and passphrase, never backs up.
 public bool RestoreOnly { get; set; } public Guid? SourceManagedJobId { get; set; }
 public bool RecoveryCopy=>SourceManagedJobId is not null;
 // Deleted by an administrator: the device keeps it only for restores (no schedule, never backs up), it is left out of
 // lists and monitoring, and the backups at the destination stay restorable from the customer page.
 public DateTimeOffset? Deleted { get; set; } public string? DeletedBy { get; set; }
 // Taken over from this device: the configuration is held back until that device's stop is confirmed, so the
 // old and the new device never write into the same backup chain at the same time.
 public Guid? HandoverFromDeviceId { get; set; }
 // Plain copy of the latest revision's schedule for monitoring (the revision itself is encrypted).
 // ScheduleRecorded=false: not yet copied (job created before this column existed). Days: bit per DayOfWeek.
 public bool ScheduleRecorded { get; set; } public int? ScheduleRepeatHours { get; set; } public int ScheduleDays { get; set; } public int KeepVersions { get; set; }
 public DateTimeOffset? ScheduleStart { get; set; }
 // Allowed age of the last successful backup in hours. null: derived from the schedule.
 public int? MaxBackupAgeHours { get; set; }
}
public sealed class ConfigurationRevision : TenantEntity
{
 public Guid ManagedJobId { get; set; } public long Revision { get; set; }
 public string EncryptedConfiguration { get; set; } = "";
 public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RemoteCommand : TenantEntity
{
 public Guid DeviceId { get; set; } public Guid? JobId { get; set; } // null for device-level commands (folder browse, destination test)
 public string RequestedBy { get; set; } = ""; public string? ApprovedBy { get; set; }
 public string EncryptedPayload { get; set; } = ""; public string Signature { get; set; } = ""; public string? EncryptedCatalog {get;set;}
 public RemoteAction Action { get; set; } public DateTimeOffset Expires { get; set; }
 public string Status { get; set; } = "Pending"; public long? TaskId { get; set; }
 public string? ErrorCode { get; set; } public string? Detail { get; set; }
 // Restore only: an administrator opened the restored files and confirmed that they are usable. This is a
 // manual statement on top of the technical result (Status Completed) and is recorded separately.
 public string? CheckedBy { get; set; } public DateTimeOffset? Checked { get; set; }
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
 public Guid Id {get;set;} public long Sequence {get;set;} public string Version {get;set;}=""; public string Platform {get;set;}="win-x64";
 public string Payload {get;set;}=""; public string Signature {get;set;}="";
 public DateTimeOffset Expires {get;set;}
}
public sealed class UpdateDeployment : TenantEntity
{
 public Guid DeviceId {get;set;} public Guid ReleaseId {get;set;}
 public string Status {get;set;}="Approved"; public string? ErrorCode {get;set;}
 public DateTimeOffset Approved {get;set;}=DateTimeOffset.UtcNow;
}
// Operator-level saved destination (e.g. the DariaTech storage server). Not tenant data: customer
// users never see it. Jobs copy URL and options at creation and get their own sub-folder.
// A saved storage location. TenantId null: offered to every customer (e.g. the DariaTech storage server);
// otherwise only to that customer (e.g. the customer's own Nextcloud). Each scope has at most one default.
public sealed class DestinationTemplate
{
 public Guid Id { get; set; } = Guid.NewGuid(); public Guid? TenantId { get; set; } public string Name { get; set; } = "";
 public string TargetUrl { get; set; } = ""; public string EncryptedOptions { get; set; } = "";
 public bool IsDefault { get; set; } public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
}
