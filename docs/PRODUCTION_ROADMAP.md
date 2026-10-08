# Production completion roadmap

Visual changes are paused at the owner's request (2026-10-08). Work on CSS, logos, installer appearance and page layouts belongs to the separate design agent. Functional work must not include the pending changes in the two engine.css files.

## Reuse and enterprise boundaries

Keep the OSS Duplicati engine, encryption/deduplication, supported storage backends, Windows VSS, schedules, filters, retention, restore, verification, repair and compaction. DariaTech supplies the management plane, not another backup engine.

The repository's `proprietary/LICENSE` explicitly requires a valid subscription for production use and restricts redistribution. Microsoft 365, Google Workspace and DiskImage modules must not enter public OSS installers without the appropriate contractual rights. A subscription to the vendor's hosted Console also does not provide its server source or imply rights to reproduce that service. Our customer/device management, policies, alerts, commands and update approvals are independently implemented. No license checks are bypassed.

## Implementation sequence and acceptance

1. Managed job configuration: encrypted immutable revisions; device-bound distribution; explicit local opt-in; durable revision journal; apply through existing engine API; acknowledgement, tenant/role/CSRF tests and actual engine create/update without modifying local jobs.
2. Remote operations: narrowly typed run/stop/verify/restore; signing and expiry; durable replay journal; restore approvals and destination safety; audits and actual execution receipts. No arbitrary HTTP path or command line from a remote caller.
3. Notifications: durable tenant-bound outbox; deduplication per alert occurrence and rule; reminders; retry and dead letter states; TLS SMTP with file-based credentials; delivery/recovery tests. Exactly-once SMTP delivery is not possible across a crash after SMTP acceptance.
4. Approved agent updates: offline trusted signing key, signed manifests, size/hash/expiry/sequence validation; independently pinned trust; staged approvals and Windows installer lifecycle tests. Authenticode requires a real signing certificate, held outside Git.
5. Operational closure: full historical runs between polls; MFA recovery and key rotation; privacy retention/erasure; storage/retention signals; migration and backup restore drill; least-privilege application-role tests; strict dependency review; Windows reboot/VSS/recovery validation.

Keep the existing service running during download/build. Fail closed on missing trust, expired/replayed commands or unconfigured delivery. Every module gets an explicit migration, negative security tests and a separate commit. Do not mark production gates complete based on documentation alone.

External release gates: working network on the owner's LXC, actual SMTP/domain credentials, signing certificate, licensed module redistribution rights and validation on supported customer Windows versions. None are synthesized or stored in Git.
