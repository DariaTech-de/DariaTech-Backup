# DariaTech Managed Backup implementation plan

Work in order; every phase has a separate commit and validation. An unchecked item is not a delivered feature.

1. [x] Baseline: pin .NET SDK, reproducible OSS-only build, engine regression tests; record evidence.
2. [x] Branding: one manifest for name/company/assets/support/colors/installer/service; ngax integration and legal notices.
3. [x] Enrollment: tenant/site-bound random single-use expiring tokens, atomic redemption; independent Windows-capable worker.
4. [x] API/PostgreSQL: explicit versioned migrations, scoped queries, secure admin login with TOTP, device auth, audit and rate limits.
5. [x] Customer/site/device management APIs, constrained tenant relationships, roles including future customer roles, scoped export and archival; physical erasure remains a release gap.
6. [x] Heartbeats, allowlisted real Duplicati job metadata, idempotent backup history; no secret/log forwarding.
7. [x] Responsive MSP dashboard and device/customer views using persisted records only; explicit empty and unknown states.
8. [x] Configurable monitoring, deduplicated persistent alerts, resolved states.
9. [x] Managed configuration revisions and local-vs-managed ownership; encrypted secrets, agent validation and acknowledgement.
10. [x] Allowlisted signed expiring remote commands, durable replay protection, audited backup/stop/restore and extra restore approval.
11. [x] Durable notification outbox, email transport, deduplication, retries and reminder rules.
12. [ ] Approved signed update manifest, artifact hashes, rollback/expiry protection, staged Windows rollout and recovery.

## Release gates

No production rollout before: negative cross-tenant tests, MFA/session tests, real encrypted backup+restore, Windows service/installer test, notification delivery test, signed update tamper/replay test, dependency/license review and operational PostgreSQL restore drill. Domain deployment requires actual hosting/DNS/SMTP credentials; a Compose file alone does not publish backup.dariatech.de.

## Delivery evidence and limits (2026-10-07)

Phases 1–8 provide a working first management slice, not the final production service. OSS server build succeeded; 66 selected upstream tests passed, 3 existing upstream skips. Seventeen management/security/engine tests passed without skips, including actual AES-encrypted backup, parsed statistics and byte-identical restore. Console and Agent builds have no warnings/errors. A Docker image was built; PostgreSQL migrations applied explicitly; the console started with a non-superuser app role and healthy readiness.

Open within this first slice: Authenticode signing and Windows reboot/VSS/customer-environment validation, incremental capture of every historical engine run between polls, complete privacy erasure/retention workflow, richer customer/site/user edit UI (management APIs exist), storage quota/retention/destination-specific alert signals, MFA recovery/key rotation and production security review. Monitoring currently covers offline, unavailable engine, missing/overdue backups, failed/warning runs and outdated agent version. It does not claim to detect every requested condition.

Phase 12 remains unimplemented and clearly labelled in the UI: approved signed updates. No execution placeholder accepts untrusted remote commands. Public DNS/hosting for backup.dariatech.de was not configured. No production admin account or demo customer data was seeded.

## Windows installer work

A bundled x64 Inno Setup EXE now builds successfully on Windows GitHub Actions. It includes the OSS engine, installs an automatic LocalSystem agent service, provisions under SYSTEM, protects state ACLs/DPAPI, isolates its engine on port 8210 and supports interactive or file-based silent enrollment. Engine jobs remain configured locally; remote configuration/restore/update gates are unchanged. The release workflow publishes an unsigned pilot prerelease only after its Windows smoke test passes. Automated smoke coverage uses a test-only HTTPS enrollment fixture and the actual bundled engine, not a production Console substitute. See AGENT.md for parameters and deployment limits.

Windows hosted CI passed the actual silent setup/service lifecycle test: insecure pre-existing state rejection, SYSTEM-bound DPAPI protection, restrictive ACLs, HTTPS enrollment against a test-only fixture, actual engine authentication, secret-free heartbeat, preserved identity on upgrade, service/engine stop and restart, and retained-state uninstall. This is evidence for the installer path on Windows Server 2025, not a claim of completed VSS/reboot/customer-PC or Authenticode validation.

Managed configuration now has Admin/CSRF-protected creation and optimistic revisions, purpose-bound encrypted definitions, tenant/device-bound agent distribution, immutable revisions, durable local anti-rollback/receipt journal, and actual engine API application. Local opt-in is required; local jobs are not adopted. This initial policy surface supports AES, sources, secure file/S3/SSH/WebDAV targets, filters, keep-versions and hourly/day schedules; advanced engine features remain local.

Remote run/stop/verification and non-overwriting restore now use signed, expiring, device-bound commands. Local opt-in and a separately pinned public key are required. Restore requires two administrators and a local RestoreRoot. Durable journaling rejects replay and reports uncertain dispatch or changed engine instance as Indeterminate. Actual engine run and duplicate-delivery tests cover execution, not arbitrary shell commands. Restore-point/file browsing through the management plane remains an additional integration task.

Email notification rules and a durable tenant-bound outbox now provide occurrence deduplication, retry/backoff, bounded reminders, cancellation and recovery. The SMTP transport requires STARTTLS and encrypts its password envelope using the Console master key. Queue behavior is integration-tested; actual provider delivery remains an operational gate pending SMTP configuration.

Phase 12 implementation now includes offline ECDSA-signed manifests, Console trust verification, monotonic approvals, device-specific staged deployments, local opt-in/pinning, streaming size/hash verification, host-restricted HTTPS redirects, installer version verification and durable update status. Automatic execution remains gated on Windows/distinct-version integration and interrupted-installation recovery validation; the phase is deliberately not checked complete yet.
