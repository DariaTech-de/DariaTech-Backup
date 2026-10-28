# Admin workflows

How an administrator takes a customer from onboarding to regular operation, keeps it running and ends the
contract, and what the Console does and does not prove at each step. The reference case is a customer with
five PCs whose backups go to the DariaTech storage server or to a third-party provider.

Evidence levels used below:

- **impl.**: implemented in this repository;
- **auto**: covered by automated tests (`DariaTech/Tests`, run with a real PostgreSQL; agent tests without an engine
  unless stated);
- **manual**: validated by hand on real devices;
- **open**: not yet proven in practice (needs real devices, storage or time).

## Principles

- **Saved is not applied.** A configuration is applied when the device confirms the revision (`AppliedRevision`).
  The device page shows "Gespeichert – noch nicht eingerichtet", "Änderung gespeichert – Gerät nutzt noch
  Revision n", "Auf dem Gerät fehlgeschlagen" or "Auf dem Gerät angewendet".
- **Requested is not completed.** A remote action is done when the device's receipt says so. Unclear outcomes
  ("Ergebnis unklar") are never executed again automatically.
- **A connection is not protection.** A device is *abgenommen* (accepted) only with engine ready, configuration
  applied, storage reached from the device, a successful first backup, a technically completed restore that an
  administrator confirmed as usable, and a known next run (or an explicitly manual job).
- **Technical evidence and manual statements are kept apart.** Restore usability, "old device will never come
  back" and "storage access revoked at the provider" are manual statements, recorded with name and time.
- **Devices back up directly to the storage.** Backup data never passes through the Console. Every connection
  test runs on the device.

## A — Customer onboarding

| What | Where | Status |
|---|---|---|
| Customer, contact, site | Kunden → Kunde anlegen; Standorte on the customer page | impl., auto |
| Expected number of devices | customer page, "Einrichtung und Abnahme" | impl., auto |
| Progress line "5 Geräte vorgesehen, 4 verbunden, 3 konfiguriert, 2 erfolgreich gesichert, 1 mit geprüftem Restore" | same | impl., auto (`FivePcCustomerIsInSetup…`) |
| Phase *In Einrichtung* until every expected device is registered and accepted, then *Regulärer Betrieb* | same | impl., auto |
| Notification recipients | `/api/v1/management/notification-rules` (no form yet) | impl.; UI open |

## B — Storage

- Global templates (all customers) and customer templates; customer templates win (impl., auto).
- Each job gets its own folder `<template>/<customer number>/<device>/<job>-<random>` (impl., auto).
- A template's connection test runs on the device against `<template>/<customer number>/<device>`, and
  "Ordner anlegen" also proves the right to create folders there (impl., auto). Testing the bare template
  folder would not show missing rights below it.
- **Separate folders are not separate permissions.** With one shared account on the DariaTech server every device
  can read and delete every other customer's folder. Use at least one storage account per customer (a customer
  template with its own credentials). The Console cannot check which rights a credential grants; this is an
  operating rule (open: per-customer accounts on the DariaTech storage server).

## C — Connecting five PCs

- "Gerät hinzufügen" issues up to 25 single-use codes in one step, each with its own install command (impl., auto).
  Each code works for exactly one device and only until it expires; a reused code is refused (auto).
- "Registrierungscodes" lists open (= registration pending), used (with the device) and expired codes without the
  code itself (impl., auto).
- A reinstalled PC enrolls as a new device. Use "Gerät ersetzen" there (see I).
- Per device the setup table shows: agent connected / engine ready, configuration pending / applied / failed,
  storage reached, first backup pending / running / succeeded / failed, restore check, next run (impl., auto).

## D — Configuring backups

- Job form: sources (folder browser on the device), exclusions, destination, passphrase (generated, revealable for
  administrators, audited), schedule, retention, allowed backup age (impl., auto).
- **First run is entered in German local time** and converted with the offset valid on that date (a time skipped by
  the spring clock change moves one hour later). The engine keeps the wall-clock time across DST in the device's
  time zone (`Scheduler.GetNextValidTime` with `DSTAwareParseTimeInterval`) (impl., auto for the conversion; open
  on real devices across a DST change).
- **Versions are not days.** The device page shows what the retention covers, e.g. "30 Versionen ≈ 42 Tage bei
  diesem Zeitplan" for Mon–Fri daily (impl., auto).
- **No schedule = "Manuell".** Such jobs are labelled and alerted only while they have never succeeded, unless an
  allowed backup age is set (impl., auto).
- **Shared settings for several PCs**: customer page → "Gemeinsame Einstellungen". Tick the backups and the settings
  to change (schedule with optional staggered start times, retention, allowed backup age, storage credentials from
  a template). Sources, exclusions, destination folder and passphrase stay per job. A preview lists every device and
  backup with before/after; saving needs an explicit confirmation (impl., auto).
- Distribution failures and pending revisions are visible per device and raise alerts (`ConfigurationFailed`)
  (impl., auto).

## E — First backup and acceptance

The device page "Schutzstatus" leads through: configuration applied → storage reached → first backup running →
first backup succeeded → sample file restored into a new folder and confirmed usable ("Restore-Test bestätigen" on
the job's history) → next automatic run known (impl., auto).

Large first backups can take days. An interrupted backup is continued by the next run; uploaded blocks are not
sent again (engine behaviour). Staggered start times are available in "Gemeinsame Einstellungen" (impl.; open: real
multi-day first backups).

## F — Daily operation

"Warnungen" is a work list: customer and device, device state, last successful backup and its age, next run,
problem with a concrete next step, and work state *Offen* / *In Bearbeitung* (who, since when) /
*behoben* (only when monitoring no longer detects it) (impl., auto).

Overdue is judged per job (impl., auto):

- allowed age = longest regular gap of the job's schedule + 6 h, simulated like the engine's scheduler from the
  job's start (Mon–Fri daily: Friday→Monday = 72 h; every 48 h Mon–Fri: Mon, Wed, Fri, Mon = 72 h), or the job's
  explicit allowed age; locally managed jobs keep `Monitoring:BackupAgeHours` (default 24 h);
- **planned absence** per device (end date + note): offline and overdue are not alerted until then, failures still
  are; afterwards normal monitoring resumes by itself; offline devices show *Abwesend*;
- an offline device is reported as offline only, not additionally as "engine not reachable"; an offline device can
  still have a recent backup, which the work list shows.

Missed runs: when the device was off, the engine runs a missed scheduled backup **once** at its next start and then
continues with the next regular time (`Scheduler.cs`: a start time in the past is queued once). On battery the run
is skipped if "disable on battery" is set (impl. in the engine; open: observed on real laptops).

States distinguished: Gerät offline (`DeviceOffline`), Backup überfällig (`BackupOverdue`), Engine nicht bereit
(`EngineUnavailable`), Backup läuft (device progress), Erfolg, Warnung (`BackupWarning`, e.g. unreadable files),
Fehler (`BackupFailed`, `RepeatedBackupFailures`), Ergebnis unbekannt / abgebrochen (`BackupNotSuccessful`).

## G — Faults

| Case | What happens | Status |
|---|---|---|
| PC off / away | `DeviceOffline`; with planned absence suppressed until its end | impl., auto |
| Internet or Console down | engines keep their local schedules; telemetry is buffered (up to 1440 snapshots) and sent later | impl. (agent); open: long real outage |
| Storage unreachable / full | backup fails → `BackupFailed`; quota from the provider → `StorageCapacityWarning/Critical` | impl., auto |
| Credentials changed / expired | backup fails; connection test reports `TargetLoginFailed`; rotate via job form or "Gemeinsame Einstellungen" | impl., auto (Console side) |
| TLS certificate / SSH host key changed | backups fail; the connection test reports `DestinationHostKeyMismatch` with the new host key (taken over only after an explicit click) or `DestinationCertificateInvalid` with the certificate fingerprint | impl., auto |
| Source folder missing, no rights, single unreadable files | `SourceAccessDenied` on actions; warnings count as warning, never as success | impl., auto |
| Agent or engine crashed | `EngineUnavailable`; the agent restarts the bundled engine | impl. |
| Job hangs | no success → `BackupOverdue` after the allowed age; progress stays visible | impl.; no separate "running too long" alert |
| Restart during backup or remote action | run ends as cancelled/unknown → `BackupNotSuccessful`; remote action → "Ergebnis unklar", never repeated automatically | impl., auto |
| Local job database missing or damaged | `RepairNeeded`; a taken-over job rebuilds its database from the destination before its first backup | impl., auto |
| Late, incomplete or contradicting telemetry | terminal runs are immutable; implausible run details are dropped; an incomplete job list never deactivates known jobs (`InventoryIncomplete`); a refused snapshot no longer blocks newer ones | impl., auto |
| Command receipt refused by the Console (deleted, conflicting, unknown code) | recorded in the agent journal, never executed again, does not block other commands; contradictions audited as `command.receipt-conflict-*` | impl., auto |
| Repeated wrong logins | lock after 5 failures for 30 min; attempts during the lock neither count nor extend it; counter restarts afterwards | impl., auto |

## H — Restore

Guided restore (job → "Aktionen / Restore"): load versions → choose version → choose files/folders → choose target →
(second administrator approves, if there is one) → follow progress → confirm usability (impl., auto; manual on Linux
with the real engine: single files, a folder with brackets in its name and a whole top-level folder restored into a new
folder with identical checksums).

- A selected folder is restored with everything below it: the Console sends it to the engine as a folder filter, as
  the engine's own restore dialog does. Until 18.10.2026 a selected folder restored nothing and the restore failed
  with an engine error; single files were not affected.

- Default target: a new folder below the device's protected restore root. **Original location** writes missing files
  back and never overwrites existing ones: the restored version is placed next to them with a timestamp suffix
  (engine `overwrite=false`). For mass restores after mistakes or malware, restore into a new folder first.
- **Another PC of the same customer**: customer page → "Auf anderes Gerät wiederherstellen" creates a recovery copy
  (same destination and passphrase, no schedule). It can never back up into that chain (Console and agent refuse),
  and it restores **only into a new folder** — the original paths belong to the source device, which may use another
  operating system or user (impl., auto).
- Device permanently lost: the backups stay listed under the customer and restore onto any active device of the
  customer the same way.
- **Without the Console**: install Duplicati (or use the agent's bundled engine) on any machine and use "Restore
  from backup files" / `Duplicati.CommandLine restore <destination URL> --passphrase=…` with the destination URL,
  its credentials and the job passphrase. Keep passphrases also outside the Console (password manager); they are
  in the Console database encrypted with the master key (see platform operation).
- File restore only: operating system and applications are not part of file backups; a full system restore is out
  of scope. A **verification** ("Backup prüfen", monthly) checks stored data; it does not replace a restore test, and
  a technically completed restore does not prove that the files are usable until someone opened them.
- Approval rule (unchanged product decision): restores need a second administrator's approval; with only one active
  administrator account the request is approved by its author and audited as `restore.single-administrator`.

## I — Changes and device replacement

- Add a PC: issue a new code. Rename / move to another site: `PUT /api/v1/management/devices/{id}`.
- **Three different things**: restoring onto another PC (recovery copy, read-only), permanently taking over a backup
  chain (replace device), starting a new chain (new job, e.g. after a passphrase or provider change).
- **Replace device** (new device page → "Gerät ersetzen"): the jobs move to the new device as taken-over jobs. The
  old device is told on its next contact (`410 Gone`) to pause its schedules; it confirms once nothing runs, its
  agent credential is revoked then, and from then on its engine is no longer started. The new device receives the
  jobs only after that confirmation — or after an administrator states that the old device will never come back
  (recorded as manual, not technical); for an old device with an agent older than 0.4 that statement must be that
  the agent was removed or the device wiped, because such agents do not understand the stop. If such a device does
  come back, it is still told to stop. Revoking alone never
  stops local schedules and never removes the old device's direct access to the storage; change the storage
  credentials where that matters. Before the new device resumes, check its sources and schedule on its device page.
  (impl., auto; manual on Linux with the real engine: the old device confirmed the stop within seconds, its engine
  stopped, and the new device continued the same version chain, uploading only new blocks; open: an old device that
  comes back online after a long time, agents ≤ 0.3.1 do not understand the stop.)
- Agent 0.4.0 pauses on the stop but never confirms it once it has run any backup or restore: it took the engine's
  last progress report, which the engine keeps after a task ends, for a running task. Fixed in 0.4.1. For an old
  device with 0.4.0, install 0.4.1 there (it then confirms at once) or confirm the stop manually on the new device.
- **Delete a device** ("Gerät löschen" on the device page, `DELETE /api/v1/management/devices/{id}`): the same stop
  without a replacement. The device leaves lists and monitoring at once (its open alerts are closed) and is listed
  under "Gelöschte Geräte" on the customer page with its stop state. If it is still switched on it pauses its
  schedules on its next contact and confirms; its credential is revoked then. A device that never comes back needs
  nothing more; a deleted device gets no configurations or commands. Its backups stay under "Backups &
  Wiederherstellung" and can be restored on another device. Agents ≤ 0.3.1 do not understand the stop and must be
  uninstalled (the customer page says so). A deleted device can still be chosen in "Gerät ersetzen".
  (impl., auto; manual on Linux with the real engine and agent 0.4.1: hidden at once, the agent confirmed the stop
  within a minute, archives untouched.)
- **Delete a backup** ("Backup löschen" on the job's actions and configuration pages, "Löschen" in the customer's
  backup list, `DELETE /api/v1/management/managed-jobs/{id}`): the job leaves lists and monitoring at once and
  gets a new revision without a schedule; the device keeps the engine job and its local database, so it no longer
  backs up on its own, and the Console refuses `RunBackup` and `VerifyBackup` (also ones queued before the deletion
  are no longer delivered). If the device does not accept the schedule removal, the customer page says so and
  monitoring raises "Gelöschtes Backup: Zeitplan nicht entfernt". The versions at the destination are
  never touched, and the passphrase stays encrypted in the Console. The backup is listed under "Gelöschte Backups"
  on the customer page and stays restorable on its own device (also to the original paths) and through "Auf anderes
  Gerät wiederherstellen". Deleting a recovery copy ("Verbindung entfernen") only hides that connection; connecting
  the same backup to that device again restores it. Deleting
  cannot be undone in the Console; to protect the data again create a new job. To remove the data, delete it at the
  destination. (impl., auto; manual on Linux with the real engine and agent 0.4.1: the engine schedule was removed,
  a file was restored byte-identically to its original path afterwards.)
- **Removing the agent from a device** (device page "Agent vom Gerät entfernen", overview `/AgentRemoval` for
  Windows, Linux and macOS): first delete or replace the device in the Console, then remove locally with the commands
  shown for the device's platform — they use fixed paths and work for every agent version. Removal keeps the local
  identity and job databases for a reinstallation of the same PC; the second command deletes them (clean removal),
  the third one also the restored files. Agents from 0.4 install `/usr/local/sbin/dariatech-agent-uninstall`
  (`--purge`, `--purge-restores`) on Linux and macOS; on Windows the uninstaller in "Apps" is used. A device that
  could not confirm its stop (offline, agent ≤ 0.3.1) needs no further step when it was deleted; when it was
  replaced, the removal is confirmed on the new device. If it does come back with an agent, it is still stopped. Backups at the storage are never touched
  (impl., auto for the Console; the scripts are covered by the native Linux/macOS smoke test in CI; open: Windows
  data deletion on a real PC).
- Changing sources, schedule or retention: new revision of the same chain. Changing destination or passphrase
  of a job is refused: that is a new chain (new job).
- **Rotate storage credentials**: per job in the job form, or for several jobs of a customer from a template in
  "Gemeinsame Einstellungen" (only jobs below that template; never other customers) (impl., auto).
- **Change provider** as a migration: create a new job to the new storage, test it from the device, run a first
  backup, test a restore, keep the old job (or its recovery copy) during a transition period, then remove the old
  backups at the old provider after approval and the agreed period. There is no automatic data migration, and
  nothing is changed silently.

## J — Regular review and contract end

Regular: capacity alerts and storage per device, overdue/failed alerts, agent updates (failed updates are shown on the
device page and alerted as `AgentUpdateFailed`; an update waits until no backup runs), sample restores (setup table,
"Restore geprüft"), device list vs. expected devices, notification delivery (`Deliveries`, retries).

Contract end (customer page → "Vertragsende"), each step confirmed and audited (`contract.*`), none implying another
(impl., auto):

1. end date, archive retention date, handover/export note;
2. stop local backups (per-device confirmation shown; also for an archived customer);
3. lock management access of the customer's users;
4. archive (no monitoring, no new devices) — does not stop devices and deletes nothing; can be reactivated;
5. storage access revoked at the provider — manual statement;
6. approve archive deletion — only after the retention date; the deletion itself happens at the storage, the
   software never deletes archives;
7. close.

Management data is erased only with the separate `--erase-customer` command (dry run by default).

## Platform operation

- Back up PostgreSQL **and** the master key separately (`docs/DEPLOYMENT.md`); losing the master key makes job
  passphrases, storage credentials and the data-protection keys unreadable. Restore both into an isolated
  environment regularly (open: scheduled restore drill).
- Command signing key (`Commands:SigningKeyFile`, encrypted with the master key) and the offline update signing key
  must be backed up with the master key; agents pin the command public key at installation.
- With the Console down, devices keep backing up on their local schedules; restores are possible without the
  Console as described in H.
- Logs, test output and documentation never contain passphrases or credentials; receipts carry only error codes.

## Runtime validation on 18.10.2026

Built from `master` and run end to end on Linux (x64, real engine, PostgreSQL, HTTPS), driven through the Console in a
browser: installation with the command the Console generates (published 0.4.0 package), onboarding of a customer
with expected devices up to acceptance, single-use codes (a used code is refused), a managed job applied on the
device, first backup, restore and its manual confirmation, device replacement and decommissioning through contract
end, schedule-aware overdue (Mon–Fri vs. daily via "Gemeinsame Einstellungen"), planned absence, work list, account
lock, the legacy-agent rule, agent removal with the Linux commands from `/AgentRemoval`, and the update command
against a real `docker compose` deployment (a missing bind address stops it before anything changes). Found and
fixed on the way: the stop confirmation of agent 0.4.0 (see I), folder restore (see H), and a one-minute false
"Verwalteter Backup-Job fehlt" alarm with a wrong `job.removed-from-device` audit right after a job was first
applied (the agent reported its job list from before applying; the Console now keeps a confirmed job until a
heartbeat lists it, at most a day, and agents from 0.4.1 report the list after applying). Not covered: Windows and macOS
devices, approved remote agent updates, credential rotation at a real provider, a real DST change, e-mail delivery.

On a device whose clock runs in another time zone than the Console (for example a Linux server on UTC), the engine
evaluates the allowed weekdays in the device's local time, so a "Mon–Fri 01:00" job runs Tue–Sat 01:00 German time;
the Console's overdue rule assumes German weekdays. Set such servers to Europe/Berlin or choose all days.

## Remaining gaps and practical tests still needed

- Notification rules have no form yet (API only).
- No alert for a backup that runs far longer than usual.
- Per-customer storage accounts on the DariaTech storage server are an operating rule, not enforced.
- Practical tests still required with real devices and storage: five-PC onboarding end to end, a full DST change on a
  device, a replaced device coming back online, a long Console outage, storage full at a real provider, credential
  rotation at a real provider, restore without the Console, multi-day first backups over slow links.
