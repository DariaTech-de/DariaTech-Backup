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

## Centrally managed job configurations

`POST /api/v1/management/devices/{deviceId}/managed-jobs` (Admin, cookie + CSRF): `{ expectedRevision: 0, definition: { name, sources, targetUrl, passphrase, backendOptions, keepVersions, filters: [{include,expression}], schedule: null | {start,repeatHours,days} } }`. Passphrase: 14–200 characters. Schedule days use .NET DayOfWeek integers (Sunday=0). Source paths must be absolute, target URL must not embed credentials. Supported managed targets: file, s3, ssh, webdav/webdavs; S3/WebDAV require `use-ssl=true`, SSH requires an explicit `ssh-fingerprint`. Credential options are encrypted with the entire immutable revision, not returned by management reads. No script/custom module/DB-path options are accepted.

`PUT /api/v1/management/managed-jobs/{id}` uses the same envelope with the current expectedRevision. A stale writer receives 409. Changing passphrase or destination requires creating a new job/chain; changing them in place receives `NewBackupChainRequired`. `GET /managed-jobs` returns metadata and application state only.

`GET /api/v1/agent/configurations` requires the device's Bearer credential AND X-Device-Id. It returns at most 20 pending latest assignments, exclusively for that active device and active customer. Contains credentials: HTTPS only; no-store; never print responses in RMM logs. `POST /api/v1/agent/configurations/{jobId}/receipt`: `{revision,localJobId,status}`; status Applied/Rejected/Failed. Applied receipts are idempotent, audit logged, and mark the corresponding telemetry job Managed. Local jobs are not automatically adopted or overwritten.

## Signed remote operations

`POST /api/v1/management/commands` (Operator + CSRF): `{jobId, action, restore, validMinutes}` with action 0 RunBackup / 1 StopBackup / 2 VerifyBackup / 3 Restore and expiry 1–30 minutes. Restore input: `{snapshot,paths,destinationFolder}`; destinationFolder is a simple directory name under the locally provisioned RestoreRoot. Requests enter `AwaitingApproval` for restore; a **different** Admin must call `POST /commands/{id}/approve` before expiry. Technicians cannot request restores. `GET /commands` exposes metadata/state only; payloads are encrypted in PostgreSQL. Signing is mandatory; unconfigured signing returns 503 and dispatches nothing.

Agents use `GET /api/v1/agent/commands` and `POST /api/v1/agent/commands/{id}/receipt` with Bearer/X-Device-Id. ECDSA P-256 signatures cover exact payload bytes, command UUID, tenant, device, local job, parameters and expiry. Receipts: Accepted, Completed, Failed, Rejected, Indeterminate; fixed error codes, never raw engine errors. Accepted means queued, not successful. Terminal receipts cannot revert to Accepted. All request/approval/outcome transitions are audited.

## Notification rules and delivery state

Admin/CSRF: `POST /api/v1/management/notification-rules` and `PUT /notification-rules/{id}` accept `{tenantId,recipient,repeatMinutes,enabled}`. Email only initially; repeat interval 60–43200 minutes. Recipient must be a plain valid email address without header/display-name injection. `GET /notification-rules` and `/notification-deliveries` are tenant scoped. Disabling a rule cancels pending deliveries. Transport is behind `INotificationTransport` for later Teams/Slack/Webhook/SMS implementations; unimplemented transports are not selectable or advertised as delivered.
