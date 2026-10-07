# Security model and current limits

## Implemented

- Console enforces HTTPS in Production except health probes. Reverse proxy headers are trusted only from explicit proxy addresses. Caddy manages certificates; PostgreSQL is not publicly exposed.
- Administrator/operator and future customer roles are distinct from device identities. Customer roles have mandatory tenant claims; queries are scoped and composite foreign keys prohibit cross-tenant parent relationships. Cross-tenant lookups return 404. Internal MSP notes are omitted from customer API/export results.
- Passwords use ASP.NET Identity V3 PBKDF2-HMAC-SHA512, 210,000 iterations, random salts. Mandatory RFC 6238 TOTP has a persisted consumed step, preventing code replay. Account attempts and step consumption are serialized in PostgreSQL; five failures lock the account for 30 minutes. IP limits also cover login and enrollment.
- Cookies are HttpOnly, SameSite=Strict and Secure in Production; fixed 30-minute sessions. Database session versions revoke disabled/changed accounts. Forms and management API mutations require antiforgery tokens. Agent routes do not accept cookies as device credentials.
- Random 256-bit enrollment tokens are stored as SHA-256 hashes and redeemed atomically once, with expiry and fixed tenant/site. Device credentials are independently generated and stored as hashes. Revocation denies further telemetry.
- AES-256-GCM encrypts management secrets with tenant/purpose associated data. ASP.NET Data Protection XML keys are encrypted by the same external master key. Store master key separately from database/key volume backups. Never lose it: TOTP secrets and sessions cannot be recovered without it. A future Vault implementation can replace ISecretStore.
- Windows agent identity/engine credential/outbox use DPAPI CurrentUser under the service account. Unix uses an explicit external 256-bit key and restrictive directory/file permissions. Console traffic requires HTTPS; engine traffic is authenticated and loopback-only. Redirects are disabled, avoiding credential redirection.
- Typed telemetry excludes paths, destinations, options, passphrases, raw logs and exception strings. Allowed error codes are BackupFailed/BackupWarning/EngineUnavailable/Cancelled/EngineOperationFailed. Unknown operation statistics remain null. Statistics are bounded/validated; terminal runs are immutable and deduplicated.
- Self-hosted UI assets, output encoding, CSP, frame denial, nosniff, referrer policy and HSTS. No external scripts/analytics. Input sizes and request body size are bounded.
- Audit events record login outcomes, provisioning, enrollment, customer/site/storage changes, device revocation, logout and exports. A PostgreSQL trigger rejects audit UPDATE/DELETE. The running web app has no migration/superuser credentials.

## Explicit release gaps

Remote backup/restore/configuration/update APIs do NOT exist yet. No untrusted command can be executed by this implementation. Design requirement for those phases: allowlisted typed commands, role checks and tenant-bound authorization, signature, device audience, command ID, expiry, persisted single-use execution receipt, audit record and an extra restore approval. Signing alone does not prevent replay. Arbitrary shell execution and arbitrary engine API forwarding are prohibited.

No MFA recovery codes/reset workflow, distributed session inventory, production penetration test or automated master-key rotation is implemented. Bootstrap uses a protected local password file and writes a one-time TOTP provisioning file, which must be deleted after enrollment. Windows service/DPAPI/VSS and installer signing require Windows validation. UI administration of roles is limited to the implemented actions; do not claim a customer portal is production-ready merely because scoped roles pass tests.

EF query filters are defense in the application, not PostgreSQL row-level security. IgnoreQueryFilters is limited to token redemption and device authentication with subsequent scope assignment. Raw SQL/schema owner access bypasses application tenant protection; do not give application credentials to customer users. Audit append-only behavior is not an externally notarized tamper-proof ledger; database owners can change triggers.

Secrets never belong in repository, SMTP test output or ordinary logs. Keep Include Error Detail=false and EF sensitive-data logging disabled. Run dependency audits, security integration tests, actual restore tests and a PostgreSQL disaster-recovery drill before production.

## Data protection / DSGVO

Collect hostname, OS/version, contact timestamps, numeric backup statistics and required customer contact data. IP addresses, file lists and source paths are not centrally collected. Process backup metadata under the customer's documented agreement; choose EU storage/hosting and document processors. Export is tenant-scoped; current API caps run export at 10,000 and explicitly reports truncation.

Device removal currently revokes credentials and archives the device; it is NOT physical erasure. Tenant erasure/retention automation is not implemented. Operational deletion must remove management history/contact data with controlled SQL while preserving required pseudonymized audit evidence and retention obligations. Retain engine/storage backups according to customer policy independently. Do not silently prune backup versions or audit records.
