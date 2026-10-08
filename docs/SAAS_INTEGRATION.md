# Managed Microsoft 365 and Google Workspace backups

The management plane uses the original Duplicati source and restore providers.
It does not reimplement Microsoft Graph or Google APIs and does not replace
provider entitlement checks.

## Implemented management workflow

On a device, choose **Neue verwaltete Konfiguration** and select Microsoft 365 or
Google Workspace. An Administrator or SuperAdmin can create and revise the job.
The API is `POST /api/v1/management/configurations` and
`PUT /api/v1/management/configurations/{id}`. The existing tenant filters,
composite foreign keys and agent authentication apply to these jobs.

A SaaS definition has an empty filesystem `Sources` array and a typed `Saas`
source. Microsoft 365 uses its directory tenant ID, application client ID and
client secret. Google Workspace uses a domain-wide delegated service account,
a Workspace administrator in the configured domain and its private JSON key.
Central Workspace jobs intentionally require domain delegation: a refresh token
and a claimed administrator address do not establish domain isolation.

Source and destination credentials and the AES backup passphrase are encrypted
inside immutable configuration revisions using the secret-store abstraction.
Management list/read APIs never return these credentials. The edit page leaves
secret inputs blank; blank fields on an update preserve the previous secret.
A new backup chain is required when changing its cloud tenant, provider,
destination or encryption passphrase. No credentials appear in signed commands,
telemetry or provider restore URL parameters.

The agent translates this definition into native Duplicati provider mounts and
settings. Native schedules, encryption, filters, retention, incremental storage
and supported destination backends continue to run in the original engine.
Provider metadata content is retained because cloud restore requires it.

## Local authorization and license boundary

Public OSS agent installers exclude subscription-restricted assemblies. Use a
dedicated official engine installation selected by a local administrator with
`Agent:ExternalEngineExecutable`; the agent starts a separate private database
and never adopts another running Duplicati service or database. Local ownership,
replacement permissions and all adjacent assemblies are validated before launch.

For production, locally configure:

```json
{
  "Agent": {
    "ManageEngine": true,
    "ExternalEngineExecutable": "C:\\Program Files\\Duplicati\\Duplicati.Server.exe",
    "AllowAgentUpdates": false,
    "AllowManagedConfiguration": true,
    "AllowRemoteCommands": true,
    "CommandPublicKeyFile": "C:\\ProgramData\\DariaTechBackup\\commands.pub",
    "AllowSaasWorkloads": true,
    "AllowedSaasTenants": ["11111111-1111-1111-1111-111111111111", "customer.example"],
    "AllowSaasRestore": false
  }
}
```

Install the vendor license through its supported local mechanism, not through
the Console. The agent checks native provider availability, valid license status
and feature seats for selected roots. Original provider seat enforcement remains
active. A usage subscription does not imply redistribution rights.

The [vendor license](../proprietary/LICENSE) explicitly permits development and
testing without a subscription. The isolated test mode additionally requires
`AllowUnlicensedSaasDevelopment=true`, `DOTNET_ENVIRONMENT=Development` and
`DARIATECH_SAAS_TESTS=1`. It leaves native development-seat limits intact.
Never enable this mode on a customer service or include restricted binaries in
public release artifacts.

Removing a local opt-in prevents new central configurations and remote actions.
Already configured native schedules remain engine jobs; suspend or delete those
locally when revoking their authorization.

## Backup outcome and cloud restore

The device's job actions page offers backup, stop, verification and encrypted
restore catalog retrieval. For managed cloud jobs, Warning is treated as Failed:
partial enumeration, rejected access or seat-limit coverage must not appear as
a complete successful tenant backup. Native result text stays local; only
allowlisted structured statistics and error codes are transmitted. Signed task
receipts reconcile the particular native backup result, not merely a Completed
queue status.

Provider restore is a separate typed action `RestoreSaas`, not a filesystem
restore. An administrator selects a snapshot, catalog paths, a provider target
and explicitly confirms writes. A different Administrator/SuperAdmin must
approve the request. Existing expiry, ECDSA signature, replay journal, tenant
scope, device identity and audit enforcement apply. The agent additionally
requires `AllowSaasRestore=true`, its cloud tenant allowlist, the exact applied
configuration revision and paths inside that job's virtual source mount.

Native provider restore uses stored metadata and the selected job's encrypted
credentials. Restoring cloud objects can write or replace objects in the selected
provider destination. Test and review the native provider destination grammar
for each workload before authorizing customer operations. Local file recovery is
available separately through the existing protected restore-root workflow.

## Verification and remaining operational acceptance

The duplicate-option regression introduced during managed-source integration
is documented in [SaaS configuration correction](SAAS_CONFIGURATION_FIX.md).
The correction preserves required source checks and repairs equivalent legacy
duplicates through normal configuration updates. Existing affected jobs require
both the corrected agent and engine; conflicting legacy values remain rejected.

```sh
dotnet run --project tests/SaasIntegration/SaasIntegration.csproj -c Release
dotnet test DariaTech/Tests/DariaTech.Tests.csproj -c Release
```

`.github/workflows/saas-development.yml` also builds the complete original engine
for development only and runs `NativeSaasTests` against its real authenticated
REST API. Both providers accept actual managed configurations. Explicit negative
tests call original provider authentication with synthetic invalid credentials
and require Failed receipts, Failed history and no leaked credential text.
This workflow never uploads restricted binaries or publishes an installer.

PostgreSQL integration tests cover encrypted revisions, cross-tenant denial,
immutable directory identity, two-administrator approval and the actual Console
forms. These checks and native negative authentication passed on commit
`c09ecf7426c8150f2513ad5340b3126a4bd5e777`.

Successful live cloud backup and object restore still require dedicated
Microsoft/Google test tenants, administrator consent and real credentials.
Validate each advertised workload, initial/incremental backup, pagination,
throttling, token expiry, seat coverage, retention and actual restored content.
CI's invalid-credential tests are not evidence of a successful live cloud restore.
Do not put OAuth data, private keys or subscriptions in Git, workflow inputs,
process arguments, test output or public artifacts.
