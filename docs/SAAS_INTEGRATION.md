# SaaS development integration

The original Office365 and GoogleWorkspace modules are connected to a separate
development-only harness at `tests/SaasIntegration`. This project is intentionally
outside `DariaTech.slnx`, the console/agent dependency graph and release packaging.

The [vendor license](../proprietary/LICENSE) expressly permits development and
testing without a subscription. Production requires a valid subscription for the
features and seat counts used. No license checks are patched, no key is invented,
and public OSS installers continue excluding restricted modules.

## Run offline checks

```sh
dotnet run --project tests/SaasIntegration/SaasIntegration.csproj -c Release
```

The harness runs without OAuth credentials or a license key. It verifies original
backup and restore provider loading, required stored metadata, password-typed
credential fields, rejection of missing credentials, and unchanged upstream
development-seat behavior. Upstream's five default development seats do not
establish production entitlement. These checks do not read or modify a live tenant.

GitHub Actions runs the isolated build and harness. It never uploads restricted
binaries or publishes an installer.

## Live integration remains a separate gate

A working provider constructor is not proof of a working backup. Live acceptance
requires the official license, dedicated non-production Microsoft/Google tenants,
application consent, least-privilege access and a designated restore destination.
Never put OAuth tokens, service-account JSON, certificate passwords or license
keys in workflow inputs, command arguments, Git, test output or public artifacts.

The management console's existing centrally managed file-job configuration and
local-filesystem restore commands do not yet create or restore SaaS workloads.
Do not route provider restore URLs through a filesystem restore operation.
SaaS job models, secret provisioning, entitlement/
coverage reporting and approved provider-specific restore commands still need
implementation and operational tests before advertising managed SaaS support.

For each advertised workload, verify encrypted initial and incremental backup,
pagination, expiration/throttling behavior, seat-limit coverage warnings, retention
and an actual restore. Preserve original metadata settings needed for restoration.
An upstream seat-limit warning must never be presented as complete tenant coverage.

## Locally selected Windows engine

The Windows agent accepts an administrator-owned local setting:

```json
{
  "Agent": {
    "ManageEngine": true,
    "ExternalEngineExecutable": "C:\\Program Files\\Duplicati\\Duplicati.Server.exe",
    "AllowAgentUpdates": false
  }
}
```

In JSON, use escaped backslashes (or forward slashes). Select a dedicated official
installation; the agent starts it with its own private database, credential and
encryption key. It never adopts another running service or database. Stop any
conflicting local service first. Configure its license through the vendor's
supported local mechanism; do not send the key to the management console.

The installation, all descendants and ancestry are checked for privileged
ownership, replacement permissions and links before launch. User-writable
assemblies are rejected even when the executable itself is protected. Windows
installer upgrades preserve the local selection. Automatic agent updates are
disabled with an external engine, keeping the vendor engine under independent
local change control. Manual agent installer upgrades remain possible.

Existing locally created engine jobs can report status through the agent.
Central SaaS job creation and provider-specific remote restore remain unavailable.
Windows CI tests external selection, permission rejection and upgrade preservation
with an OSS engine fixture; it does not substitute that fixture for SaaS backup.
