namespace DariaTech.Contracts;

// Deliberately excludes source paths, destination URLs, options, logs and passphrases.
public enum RunStatus { Unknown, Running, Success, Warning, Failed, Cancelled }
public sealed record EnrollmentRequest(string Token, string Hostname, string OperatingSystem, string AgentVersion);
public sealed record EnrollmentResponse(Guid DeviceId, string Credential);
public sealed record JobReport(string LocalId, string Name, DateTimeOffset? NextRun, RunReport? LastRun);
public sealed record RunReport(string LocalRunId, DateTimeOffset Started, DateTimeOffset? Completed,
    RunStatus Status, long? Bytes, long? Files, long? StorageBytes, double? Progress, string? ErrorCode,long? QuotaFreeBytes=null,long? QuotaTotalBytes=null,bool? QuotaWarning=null,bool? QuotaError=null,bool? RetentionError=null);
public sealed record ProgressReport(string LocalJobId,long TaskId,double Fraction,long Bytes,long Files);
public sealed record HeartbeatRequest(string AgentVersion, string OperatingSystem, bool EngineReachable, JobReport[] Jobs,ProgressReport? ActiveOperation=null);
public sealed record RunHistoryItem(string LocalJobId,long RecordId,RunReport Run);
public sealed record HistoryRequest(RunHistoryItem[] Runs);
