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
