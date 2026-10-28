# Storage locations and recovery to another device

The model follows MSP backup consoles such as Acronis Cyber Protect Cloud: DariaTech (partner) → customer
(tenant) → site → device. The Duplicati engine on each device does all backup and restore work; backup data
goes directly from the device to its destination and never through the Console.

## Storage locations

`Verwalten → Speicherziele` holds saved destinations with a scope:

- **All customers** (`TenantId` null), e.g. the DariaTech storage server.
- **One customer**, e.g. that customer's own Nextcloud. Only that customer's devices are offered it, and its
  options are encrypted for that tenant.

Each scope has at most one default. A new job of a customer preselects the customer's default, then the
default for all customers. Every job gets its own folder below the location: `customer number / device / job`.
Changing a location affects new jobs only; existing jobs keep their configuration.

## Recovery to another device

`Kunde → Backups & Wiederherstellung` lists every backup job of the customer, including those of offline or
deactivated devices. "Auf anderes Gerät wiederherstellen" creates a **recovery copy** on another active device
of the same customer:

- same destination URL, backend options and passphrase as the latest revision of the source job;
- no schedule, `RestoreOnly=true` (enforced by `ConfigurationPolicy`, fixed for the lifetime of the job);
- the agent skips source authorization for it, tags the engine job `DariaTechRestoreOnly` and refuses
  `RunBackup`; the Console refuses to queue `RunBackup` as well, because two devices writing one destination
  would mix their data.

The recovery copy has no local database. The engine reads restore points and the file list directly from the
destination (the equivalent of Duplicati's "Restore from backup files": connect the folder, enter the
passphrase). File listing therefore uses a filter (`*` or the entered catalog filter) instead of folder
browsing. Restores go into a new folder below the device's protected restore root and need a second
administrator's approval, as for any restore. The device needs the managed-configuration and remote-command
opt-ins (see `AGENT.md`).

SaaS and Proxmox jobs are recovered through their own restore actions and are not copied.

Without the Console, any backup can still be restored with stock Duplicati given the destination access and the
passphrase.

## Replacing a device

When a device is reinstalled or replaced, the new installation enrolls as a new device. Its device page offers
"Gerät ersetzen – Backups übernehmen" for any other device of the same customer that has backups; a device with
the same name is suggested first. Taking over:

- moves every file backup job of the old device to the new device as a new revision with the same destination,
  passphrase, sources and schedule, marked `Adopted=true`;
- deactivates the old device and requests its **stop** (`RetireRequested`); the moved jobs carry
  `HandoverFromDeviceId` and are **not delivered to the new device** until the old device's stop is confirmed,
  so two devices never write into one destination at the same time;
- leaves SaaS and Proxmox jobs on the old device; they are set up again on the new host.

The stop is confirmed in one of two ways, and the Console shows which one applied:

- **technically**: the next time the old agent contacts the Console, its heartbeat is answered with
  `410 Gone`. The agent pauses its engine without a time limit (no further scheduled runs), waits until a running
  operation has finished, then calls `POST /api/v1/agent/retired`. Only then is its credential revoked
  (`device.retire-confirmed`). From then on the agent no longer starts its bundled engine at all (the engine
  does not keep a pause across restarts, and a restarted scheduler would catch up a missed run). Local job
  databases are kept. Until the confirmation the pause is re-applied every cycle; an engine crash in that short
  window could still start one catch-up run. Agents with an external engine (`ManageEngine=false`) can only pause.
- **manually**, for an old device that will never be online again (wiped, broken, lost): an administrator
  confirms that on the new device's page (`device.retire-manually-confirmed`). This is a statement, not a proof.
  The old credential is deliberately left valid: if the device does come back, it is still told to stop.
  For an old device with an agent older than 0.4 (which does not understand the stop) the Console additionally
  requires the statement that the agent was uninstalled there or the device was wiped
  (`device.agent-removal-confirmed`); otherwise approve the agent update and switch the device on once.

Revoking an agent does not stop its local schedules, and none of this removes the old device's own access to the
storage. Where that matters (stolen device, untrusted former owner), change the storage credentials at the
storage provider and update the job on the new device with them. Agents of version 0.3.1 and older do not understand
`410 Gone` and keep backing up until they are updated or uninstalled; the stop stays unconfirmed for them.

"Gerät löschen" (device page, or `DELETE /api/v1/management/devices/{id}`) requests the same stop without a
replacement and hides the device at once. The backups stay at the destination and can be restored on another device
of the customer ("Backups & Wiederherstellung" on the customer page).

"Backup löschen" (`DELETE /api/v1/management/managed-jobs/{id}`) removes only the job's schedule on the device; the
engine job, its local database and the versions at the destination stay. A deleted backup is listed under
"Gelöschte Backups" on the customer page and restores like any other backup, on its own device or as a recovery
copy on another one.

On the new device the agent creates the engine job **without a schedule**, removes any local database of that
job and runs the engine's repair, which recreates the database from the destination. Until that has succeeded
the agent sends no configuration receipt and refuses `RunBackup` and `VerifyBackup`. A failed rebuild is reported
as a failed configuration and retried after 30 minutes. After the rebuild the job gets its schedule and the next
backup continues the existing version chain.

## Restoring from the Console

`Backup-Job → Wiederherstellen` guides through three steps: load the versions from the device, load the files
of one version (folder by folder; a recovery copy lists files by filter), choose the target:

- **new restore folder** below the device's protected restore root (Windows
  `C:\ProgramData\DariaTechBackup\Restores`, Linux `/var/lib/dariatech-backup-restores`, macOS
  `/Library/Application Support/DariaTechBackup/Restores`), or
- **original location**: missing files are written back; existing files are never overwritten, the engine
  places the restored version next to them.

With two or more MSP administrators a restore waits for a second administrator's approval; with a single
administrator it starts immediately and is audited as `restore.single-administrator`.

Remote actions require the device's local opt-in. The add-device install command sets it ("Fernaktionen aus
der Konsole zulassen"): it pins the Console's public command key and creates the restore root. Without a
configured `Commands:SigningKeyFile` the Console creates its key on first use, encrypted with the master key in
the key directory.

## Monthly check

The Console queues a `VerifyBackup` for every job with a successful backup whose device is online, 30 days
after the last successful check (retried daily otherwise). The engine downloads sample volumes and checks them;
a failed check raises the `BackupVerificationFailed` alert. Set `Verifications:Enabled=false` to disable.
