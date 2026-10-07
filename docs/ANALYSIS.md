# Duplicati baseline analysis

Baseline: `a3061c912`, master, analysed 2026-10-07. This is a structural and security-focused analysis of all project definitions and the relevant execution paths, not a claim of a line-by-line audit of every source file.

| Area | Implementation | DariaTech decision |
|---|---|---|
| Runtime | .NET 10, C#, .slnx; Linux/macOS/Windows | Pin SDK; preserve upstream projects |
| Engine | Library/Main Controller and Operation/*, block deduplication, compression, AES encryption, verification and restore | Reuse without reimplementation |
| Backends | Library/Backend/*, dynamic loader, S3, SSHv2/SFTP, WebDAV, File and many others | Reuse OSS backends |
| Server | Server/Program, WebserverCore ASP.NET Core Kestrel, task queue, scheduler, cancellation | Loopback-only engine service |
| API | WebserverCore/Endpoints V1/V2; /backups, /backup/{id}/run, /restore, /filesets, /files, /progressstate, /serverstate | Explicit adapter; never proxy arbitrary remote paths |
| Authentication | local password, JWT refresh families, host validation and login throttle | Separate console identities and device credentials |
| Databases | SQLite server settings and per-backup block/file/volume databases, SQL migrations | Retain local DBs; PostgreSQL for management |
| UI | AngularJS ngax in tree; modern Angular ngclient downloaded from @duplicati/ngclient | Brand maintained ngax; own management UI, no broad minified replacement |
| Agent | Agent + RemoteControl registration and outbound websocket to api/app.duplicati.com, encrypted command payloads | Separate DariaTech worker, no connection to vendor console |
| Updates | Library/AutoUpdater JsonSignature, signed manifests, OEM resource hooks, package hashes | Disable upstream checks for managed profile; separate approved signed updates later |
| Windows | WindowsService service control, Service wrapper, WiX installer assets | Separate Windows worker service; installer values in shared branding |
| Packaging | ReleaseBuilder, Docker, deb/rpm, AppImage, macOS, WiX | Preserve upstream, dedicated OSS publish entrypoint |
| Secrets | SecretProvider implementations, OS stores and VaultSharp; DB encryption helpers | Keep local secrets local; management secret-store interface |
| Tests | NUnit, integration/testcontainers, Playwright | Focused engine regressions plus management security integration tests |

## Licensing boundary

Root LICENSE is MIT, Copyright (c) 2026 Duplicati Inc. `proprietary/LICENSE` is subscription-restricted and is NOT MIT. Backends and SourceProviders directly referenced LoaderHelper, which pulled LicenseChecker, Office365, GoogleWorkspace and DiskImage. DariaTechOssOnly explicitly removes these references and source paths; do not publish the entire upstream solution as an OSS product. The original source and notices remain intact. Disk image and proprietary SaaS backup features are not included. Third-party notices remain in Duplicati/License and thirdparty. Release packages need their own dependency/license inventory before distribution.

## External communications and data minimisation

UsageReporter defaults to Information in release builds, with uploads to usage-reporter.duplicati.com/api/v1/report. DO_NOT_TRACK=1 and USAGEREPORTER_Duplicati_LEVEL=none disable reporting. CrashlogHelper writes a local crash log; inspect before support export. UpdaterManager contacts updates.duplicati.com; AUTOUPDATER_Duplicati_SKIP_UPDATE=1 and --disable-update-check disable automatic checks. Agent/RegisterForRemote defaults to api.duplicati.com/remotecontrol/register and app.duplicati.com; the DariaTech worker must not launch that upstream agent or enroll there. ngclient debug startup can fetch npm packages. OAuth backends may contact provider/OAuth-helper endpoints by design; those are storage authentication, not required management telemetry. This is not an exhaustive network penetration test.

## Risks and changes

Only compile guards at the proprietary loader boundary and a branding hook in the maintained local UI are necessary in engine code. Do not rename namespaces, executable APIs or database schemas. Secrets can appear in destinations, options, sources, raw logs and errors: use an allowlisted status DTO, never transmit backup definitions or raw errors. Tenant isolation needs both constrained relationships and authorization tests. Windows VSS/service permissions and real restores need Windows validation. New devices must be marked unverified until a successful backup and restore verification exist. Do not equate heartbeat with healthy backup.
