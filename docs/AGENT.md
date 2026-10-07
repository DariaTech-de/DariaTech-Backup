# DariaTech agent

Independent .NET Worker in DariaTech/Agent; Duplicati remains a separate local engine service. Current version 0.1.0.0. The worker performs no backups itself and does not replace the existing scheduler, engine database, encryption, retention or backends.

## Build and provisioning

```sh
dotnet publish DariaTech/Agent/DariaTech.Agent.csproj -c Release -r win-x64 --self-contained true
```

Set Agent:ConsoleUrl (HTTPS origin), Agent:EngineUrl (authenticated loopback origin), Agent:StateDirectory and HeartbeatSeconds. Non-Windows hosts also require Agent:LinuxKeyFile (base64 256-bit key in a protected file). Environment variables use `Agent__...`; appsettings.json has no credentials.

```text
DariaTech.Agent.exe set-engine-credential --credential-file <protected local-engine-password file>
DariaTech.Agent.exe enroll --token-file <protected enrollment-token file>
DariaTech.Agent.exe
```

Run both provisioning commands under the exact Windows service identity (normally LocalSystem via the customer's RMM) because DPAPI CurrentUser cannot decrypt a different account's state. Restrict the state directory to SYSTEM/Administrators before provisioning. Delete input secret files afterwards. Install the published worker as the centrally configured windowsServiceName from branding/product.json with Automatic start and service recovery. No signed DariaTechBackupSetup.exe/MSI has been produced or Windows-tested yet; `/quiet /token=...` remains an installer requirement, not a working command.

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
