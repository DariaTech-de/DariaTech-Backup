# Enterprise capabilities and integration boundaries

This inventory describes executable backend capabilities. Page design and UI wiring are delegated separately; an API capability is not a claim that a corresponding page is finished.

| Capability | Implementation | Limits / acceptance |
|---|---|---|
| Encrypted file backup, deduplication, compression, retention, restore, verify, repair, compact | Existing OSS Duplicati engine | Use the engine; actual AES backup and byte-identical restore are integration-tested. |
| Windows VSS and existing storage backends | Existing OSS engine | Service account permissions, UNC access and VSS must be validated on customer Windows systems. Central policy initially supports secure file/S3/SFTP/WebDAV targets. Other native options remain local. |
| Customer/site/device/job tenancy | DariaTech Console/PostgreSQL | Compound tenant relationships, scoped authorization and negative isolation tests. Customer portal UX is not delivered. |
| MFA/RBAC/audit | DariaTech Console | Mandatory administrator TOTP, session revocation, CSRF/rate limits, local protected recovery and append-only application audit. Database owners remain privileged. |
| Enrollment/telemetry/history | DariaTech Agent | Single-use expiring tokens; protected device credential; durable history spool. Deleted upstream logs cannot be reconstructed. Raw secrets/logs are not forwarded. |
| Central backup policy | Encrypted immutable revisions + native engine APIs | Explicit local opt-in; no scripts, arbitrary modules or remote shell. Destination/encryption changes require a new chain. |
| Remote backup/stop/verify | Signed typed device-bound commands | Independent pinned trust, expiry and durable replay handling; uncertain dispatch is not rerun. |
| Restore browsing/execution | Signed catalog/restore commands + native engine APIs | Encrypted bounded catalogs; truncation explicit; two administrators approve restore into a new folder under local RestoreRoot. |
| Monitoring | Persistent deduplicated alerts | Offline, missing/overdue backup, engine failure, warning/failure, repeated failure, reported quota/retention, failed verification and outdated approved version. Missing signals remain unknown. |
| Notifications | Durable email outbox and transport abstraction | STARTTLS required; retry/dead letter/reminders/recovery. Real provider delivery is an operational gate. Teams/Slack/SMS/webhooks are extension points only. |
| Approved agent updates | Offline ECDSA manifests and verified installer | Separate pinned trust, hash/size/version/expiry/sequence checks, per-device approval and durable receipts. Windows recovery/reboot validation and Authenticode remain rollout gates. |
| Data protection / operations | Scoped export, archive, deliberate owner-only erasure and retention | Audit tombstones retained; archives, mail and old DB dumps need separate contractual disposal. Docker/explicit migrations, least-privilege startup and DB restore drill are verified. |
| Microsoft 365, Google Workspace, DiskImage | Vendor subscription-restricted modules | Excluded from public OSS installer. Valid subscription and redistribution rights must be verified before integration/distribution. No bypass of license checks. |
| Vendor hosted Enterprise Console/service | Separate vendor offering | It is not this repository's OSS server. DariaTech management is independently implemented; no promise to reproduce an unavailable proprietary service. |

A production release additionally needs real SMTP/hosting configuration, offline trust provisioning, a DariaTech signing certificate, customer restore exercises and independent security review. See SECURITY.md, UPDATES.md and PRODUCTION_ROADMAP.md for procedures and limits.
