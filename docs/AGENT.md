# DariaTech agent

Independent .NET Worker in DariaTech/Agent; Duplicati remains a separate local engine process (installer-managed child or manually installed service). Current version 0.1.0.0. The worker performs no backups itself and does not replace the existing scheduler, engine database, encryption, retention or backends.

## Bundled Windows installer

The Windows installer source is in deploy/windows. The Windows installer GitHub Actions workflow builds a self-contained x64 Agent and OSS Duplicati engine into DariaTechBackupSetup.exe. Download the [Windows pilot release](https://github.com/DariaTech-de/DariaTech-Backup/releases/tag/agent-v0.1.0-pilot.2), or its unsigned artifact from a successful workflow run. These are pilot artifacts, not Authenticode-signed production releases. Do not override security policy to deploy unsigned software across customer fleets.

Interactive setup requests the HTTPS Console origin, a site-bound enrollment token and a local engine UI password (14–200 characters). Run as administrator. Setup refuses pre-existing state directories with untrusted ownership, inherited/broad ACLs or reparse points. It returns a nonzero exit code on provisioning failure, including silent installs. Setup creates an automatic LocalSystem service with restart recovery. A Windows Job Object terminates its engine child on service termination. Initial enrollment and DPAPI protection execute inside LocalSystem; no manual SYSTEM shell is needed.

The installer owns a separate loopback engine on port 8210 and stores databases under `%ProgramData%\DariaTechBackup\engine`. It never adopts or modifies an existing Duplicati service/database. Its password and settings encryption key are DPAPI CurrentUser-protected under SYSTEM; secrets are supplied to the child via process environment, not command-line arguments. The local UI is available at http://127.0.0.1:8210/ngax/ using the chosen local password. Configure actual backup jobs there; registration alone does not create a backup job.

For RMM deployment, prefer protected input files:

```text
DariaTechBackupSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /console=https://backup.dariatech.de /tokenfile=C:\secure\enrollment.txt /enginepasswordfile=C:\secure\engine-password.txt
```

`/token=...` is supported for compatibility but exposes the token in process arguments; prefer `/tokenfile`. Silent setup can omit enginepasswordfile; the service generates a random local password, so use a protected password file if local UI access is needed. Keep input files ACL-restricted and delete the original files after successful installation; setup only deletes its internal staging copies. Do not enable installer logging with token command-line arguments.

Enrollment requires HTTPS reachable without interactive Cloudflare Access/browser challenges. Failed enrollment/startup returns an installer error and preserves state for diagnosis/retry; it does not display a successful installation. Enrollment tokens can expire/be consumed, so a failed network exchange may require a new token. Upgrades preserve identity and engine credentials/database. Uninstall removes the service and program files but deliberately retains backup databases, protected credentials and identity. Secure deletion/de-enrollment is a separate administrative action. DPAPI state cannot simply be copied to a different computer.

Build locally on Windows with .NET SDK 10.0.401 and Inno Setup 6:

```powershell
./scripts/build-windows-installer.ps1
```

The current Windows CI evidence and remaining release gates are documented in IMPLEMENTATION_PLAN.md. VSS, reboot recovery, existing customer environments and signing require validation before production fleet deployment.

## Manual build and provisioning

```sh
dotnet publish DariaTech/Agent/DariaTech.Agent.csproj -c Release -r win-x64 --self-contained true
```

Set Agent:ConsoleUrl (HTTPS origin), Agent:EngineUrl (authenticated loopback origin), Agent:StateDirectory and HeartbeatSeconds. Non-Windows hosts also require Agent:LinuxKeyFile (base64 256-bit key in a protected file). Environment variables use `Agent__...`; appsettings.json has no credentials.

```text
DariaTech.Agent.exe set-engine-credential --credential-file <protected local-engine-password file>
DariaTech.Agent.exe enroll --token-file <protected enrollment-token file>
DariaTech.Agent.exe
```

Run both provisioning commands under the exact Windows service identity (normally LocalSystem via the customer's RMM) because DPAPI CurrentUser cannot decrypt a different account's state. Restrict the state directory to SYSTEM/Administrators before provisioning. Delete input secret files afterwards. Install the published worker as the centrally configured windowsServiceName from branding/product.json with Automatic start and service recovery. The bundled EXE workflow above automates these steps; manual provisioning remains available. `/quiet` is not an Inno Setup switch; use `/VERYSILENT`.

## Engine service profile

Build/publish only the OSS server entrypoint with DariaTechOssOnly=true. Start the existing engine service automatically with a protected local database-encryption key and local API password. Keep webservice-interface=loopback; for managed hosts use webservice-api-only=true. Configure real sources, destinations, schedules, passphrase, filters and retention using the existing Duplicati local APIs/tools. Sources and encryption/storage secrets stay local. Persist both engine and per-job databases and protect their filesystem permissions.

Disable vendor communications in the managed service environment:

```text
DO_NOT_TRACK=1
USAGEREPORTER_Duplicati_LEVEL=none
AUTOUPDATER_Duplicati_SKIP_UPDATE=1
```

Also pass `--disable-update-check=true`. Do not launch the upstream Duplicati.Agent remote-registration program or enroll against api.duplicati.com. Storage-provider/OAuth requests still occur when configured; see ANALYSIS.md. When reusing an existing engine data folder, explicitly unregister vendor remote control and review persisted additional-report/send-http options. DO_NOT_TRACK alone does not disable operator-configured HTTP reporting or a previously registered remote controller.

## Reporting

Agent polls backups, serverstate, local result logs and progressstate. It maps only verified result status/statistics, job IDs/names, scheduled times and allowed error codes into the public DTO. Successful completion is never inferred merely from a finish timestamp. Generic LastErrorDate events may belong to a restore/verify/other operation; they are reported as EngineOperationFailed with unknown timing/statistics, not as a confirmed failed backup. Raw local results are processed on-device and never forwarded.

Encrypted outbox survives restarts and retries failed sends. Console deduplicates runs by job + stable local run ID; completed runs cannot be rewritten by delayed Running messages. Outbox is bounded at 1,440 snapshots; overflow discards oldest with a local warning. The current polling adapter reports the latest completed run per job, not every historical engine run between polls. A polling interval of 60 seconds is default; long offline intervals/multiple jobs may require a future incremental result cursor for complete history. Agent failures are reported as EngineUnavailable/heartbeat status without raw exception text.

Active progress is a separate snapshot, not a fabricated historical BackupRun. Console command execution, remote restore and self-update are absent until their later phase gates pass. Windows service/VSS behavior requires an actual Windows test before release.

## First installation on a Windows test PC

1. Download DariaTechBackupSetup.exe and its SHA-256 file from the pilot release; verify with `Get-FileHash .\DariaTechBackupSetup.exe -Algorithm SHA256`.
2. Generate a fresh enrollment token for the correct customer/site in the Console.
3. Run setup as administrator; enter https://backup.dariatech.de, the token and a strong local engine UI password.
4. Check that the registered device reports a heartbeat in the Console. Installation alone does not create a backup job.
5. Open http://127.0.0.1:8210/ngax/ on that PC, sign in using the local engine password and configure sources, destination, schedule, backup encryption passphrase and retention. Keep the backup encryption passphrase in your protected recovery store; it is different from the local API/UI password.
6. Run a backup and a restore test, then check the transmitted result in the Console. Do not roll the unsigned pilot out across production customers before signing and customer-platform validation.

Branding pilot.2 uses the official DariaTech assets documented in [branding provenance](../branding/README.md). Windows workflow [37693913159](https://github.com/DariaTech-de/DariaTech-Backup/actions/runs/37693913159) passed installation, enrollment, engine authentication, heartbeat, upgrade, restart and uninstall checks for this package.
