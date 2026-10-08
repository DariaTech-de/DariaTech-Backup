# DariaTech Managed Backup implementation plan

Work in order; every phase has a separate commit and validation. An unchecked item is not a delivered feature.

1. [x] Baseline: pin .NET SDK, reproducible OSS-only build, engine regression tests; record evidence.
2. [x] Branding: one manifest for name/company/assets/support/colors/installer/service; ngax integration and legal notices.
3. [x] Enrollment: tenant/site-bound random single-use expiring tokens, atomic redemption; independent Windows-capable worker.
4. [x] API/PostgreSQL: explicit versioned migrations, scoped queries, secure admin login with TOTP, device auth, audit and rate limits.
5. [x] Customer/site/device management APIs, constrained tenant relationships, roles including future customer roles, scoped export, archival and owner-authorized physical erasure.
6. [x] Heartbeats, allowlisted real Duplicati job metadata, idempotent backup history; no secret/log forwarding.
7. [x] Responsive MSP dashboard and device/customer views using persisted records only; explicit empty and unknown states.
8. [x] Configurable monitoring, deduplicated persistent alerts, resolved states.
9. [x] Managed configuration revisions and local-vs-managed ownership; encrypted secrets, agent validation and acknowledgement.
10. [x] Allowlisted signed expiring remote commands, durable replay protection, audited backup/stop/restore and extra restore approval.
11. [x] Durable notification outbox, email transport, deduplication, retries and reminder rules.
12. [ ] Approved signed update manifest, artifact hashes, rollback/expiry protection, staged Windows rollout and recovery.

## Release gates

No production rollout before: negative cross-tenant tests, MFA/session tests, real encrypted backup+restore, Windows service/installer test, notification delivery test, signed update tamper/replay test, dependency/license review and operational PostgreSQL restore drill. Domain deployment requires actual hosting/DNS/SMTP credentials; a Compose file alone does not publish backup.dariatech.de.

## Delivery evidence and limits (2026-10-08)

The management suite now covers 31 passing tests without skips, including real AES-encrypted backup and byte-identical restore, managed job create/update, signed command replay/restart handling, actual restore catalog queries, tenant isolation, notification queue/retry/recovery, retained-key decryption, MFA recovery, deliberate erasure and quota/retention/verification alerts. The OSS baseline previously passed 66 selected upstream tests with 3 upstream skips. The Console image built successfully and started read-only as a non-root process using a non-superuser application role. A fresh PostgreSQL dump/restore drill verified the migration ledger, encrypted data decryption with the original key and restored append-only audit triggers.

Windows CI verifies real installer/service enrollment, SYSTEM DPAPI and ACL protection, preserved identity and locally pinned trust settings on upgrade, service/engine lifecycle and retained-state uninstall. A further CI test builds two distinct versions and exercises tampered signatures/artifacts and the signed approved-update lifecycle; its result must be successful before publishing those binaries.

Managed configuration supports AES, sources, secure file/S3/SSH/WebDAV targets, filters, keep-versions and hourly/day schedules. Advanced engine options remain available locally. Remote run/stop/verification, restore-point/file discovery and non-overwriting restore are signed and device-bound; restore requires two administrators and a local RestoreRoot. Discovery is bounded and explicitly marks truncated catalogs. Raw error/log forwarding and arbitrary remote shells are excluded.

Email notification transport requires STARTTLS and encrypted credentials. Durable occurrence deduplication, retries, reminders and recovery are tested; actual provider delivery requires deployment credentials. The signed updater includes independently pinned offline trust, monotonic sequence approvals, HTTPS host restrictions, streaming hash/size validation and a durable installation journal. Interrupted installer recovery, Windows reboot/VSS and supported customer-PC validation remain production gates.

Remaining work outside the functional backend includes the separate design agent's UI integration, Authenticode with a real company certificate, independent security review and customer-environment validation. Vendor subscription-restricted modules are not included in OSS installers without verified redistribution rights. Domain/Cloudflare and SMTP configuration require actual operator credentials; no production accounts, customer data or fake signing trust are seeded.
