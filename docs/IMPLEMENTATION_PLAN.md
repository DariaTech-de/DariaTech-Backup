# DariaTech Managed Backup implementation plan

Work in order; every phase has a separate commit and validation. An unchecked item is not a delivered feature.

1. [x] Baseline: pin .NET SDK, reproducible OSS-only build, engine regression tests; record evidence.
2. [ ] Branding: one manifest for name/company/assets/support/colors/installer/service; ngax integration and legal notices.
3. [ ] Enrollment: tenant/site-bound random single-use expiring tokens, atomic redemption; independent Windows-capable worker.
4. [ ] API/PostgreSQL: explicit versioned migrations, scoped queries, secure admin login with TOTP, device auth, audit and rate limits.
5. [ ] Customer/site/device CRUD, constrained tenant relationships, roles including future customer roles, export/deletion strategy.
6. [ ] Heartbeats, allowlisted real Duplicati job metadata, idempotent backup history; no secret/log forwarding.
7. [ ] Responsive MSP dashboard and device/customer views using persisted records only; explicit empty and unknown states.
8. [ ] Configurable monitoring, deduplicated persistent alerts, resolved states.
9. [ ] Managed configuration revisions and local-vs-managed ownership; encrypted secrets, agent validation and acknowledgement.
10. [ ] Allowlisted signed expiring remote commands, durable replay protection, audited backup/stop/restore and extra restore approval.
11. [ ] Durable notification outbox, email transport, deduplication, retries and reminder rules.
12. [ ] Approved signed update manifest, artifact hashes, rollback/expiry protection, staged Windows rollout and recovery.

## Release gates

No production rollout before: negative cross-tenant tests, MFA/session tests, real encrypted backup+restore, Windows service/installer test, notification delivery test, signed update tamper/replay test, dependency/license review and operational PostgreSQL restore drill. Domain deployment requires actual hosting/DNS/SMTP credentials; a Compose file alone does not publish backup.dariatech.de.
