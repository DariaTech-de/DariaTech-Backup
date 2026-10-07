# API v1 management foundation

Routes use /api/v1. Breaking DTO changes require a new protocol version. JSON requests reject unknown properties, missing required constructor fields and null values for non-nullable fields. Request size is limited to 256 KiB.

## Browser/operator

Login is the CSRF-protected `/Login` form with Email, Password and six-digit Code (mandatory TOTP). Session cookie expires after 30 minutes. Anonymous management calls return 401; role violations return 403; inaccessible scoped objects return 404.

GET `/api/v1/management/csrf` returns a token. POST/PUT/DELETE management requests must send X-CSRF-Token together with the authenticated cookie. Forms include antiforgery tokens automatically.

| Route | Method | Behavior |
|---|---|---|
| /api/v1/management/customers | GET / POST | Scoped list / admin creates tenant and customer |
| /api/v1/management/customers/{id} | PUT / DELETE | Admin updates / archives customer and revokes tenant access |
| /api/v1/management/sites | GET / POST | Scoped sites / operator creates within customer |
| /api/v1/management/devices | GET | Scoped devices |
| /api/v1/management/devices/{id} | GET / PUT / DELETE | Detail/history / operator renames or moves within tenant / admin revokes and archives |
| /api/v1/management/enrollment-tokens | POST | Operator issues tenant/site-bound token, returned once |
| /api/v1/management/alerts | GET | Scoped unresolved alerts |
| /api/v1/management/audit | GET | Latest 500 scoped audit events |
| /api/v1/management/storage-targets | GET / POST | Secret-free metadata / encrypted credential creation |
| /api/v1/management/export | GET | Scoped metadata export, no credentials or internal notes for customer roles |

Enrollment issue body: TenantId, SiteId, ValidMinutes (5..1440). Customer fields: Name, Number, Contact, Email, Phone, Address, Notes, Active. Tenant IDs on management inputs are validated against authenticated permissions and existing parents; device inputs do not choose their tenant.

## Device

POST `/api/v1/agent/enroll`: Token, Hostname, OperatingSystem, AgentVersion. Returns DeviceId and a newly generated Credential once; persists only its hash. Token redemption is atomic, expiring and single-use. No cookie authenticates a device.

POST `/api/v1/agent/heartbeat`: Authorization: Bearer <credential>, X-Device-Id: <DeviceId>. Both must match the same active agent. Body follows DariaTech/Contracts/Telemetry.cs: AgentVersion, OperatingSystem, EngineReachable, Jobs, optional ActiveOperation. Each job has LocalId, Name, NextRun and optional LastRun. LastRun contains a stable LocalRunId, UTC Started/Completed, status, nullable nonnegative Bytes/Files/StorageBytes, optional bounded Progress and an allowlisted ErrorCode. Current enums are serialized as numeric .NET values.

Returns 204 when accepted; 400 invalid input; 401 invalid/revoked identity or expired/used enrollment; 429 rate limit. Retry transient errors with bounded backoff; replaying a valid run is idempotent. Never send sources, targets, encryption options, passwords or raw logs.

Progress snapshot contains LocalJobId, TaskId, Fraction [0..1], Bytes and Files. No file names. Server contact time is recorded on receipt; offline replay does not manufacture a new successful backup timestamp.

## Not implemented

There is no remote-command endpoint, arbitrary local-API proxy, central configuration distribution, restore API, email delivery API or update download/execution API. Frontend labels identify these limits. Future commands must be versioned, audited, tenant/device-bound, signed, expiring and persisted against replay.

Site PUT/DELETE routes validate tenant ownership and refuse deleting sites with device history. User GET exposes metadata only; user PUT requires SuperAdmin, preserves the last active SuperAdmin, rewraps tenant-bound TOTP secrets and invalidates existing sessions.

A generic engine error without a verified backup result is an EngineOperationFailed incident. Started carries the observed error timestamp; Completed and statistics are null because operation timing/type/counts are unknown. It must not be counted as a confirmed failed backup or fabricated zero-duration/zero-byte run. EngineOperationFailed is an additional allowed error code.
