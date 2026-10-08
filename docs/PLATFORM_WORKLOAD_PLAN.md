# Cross-platform and workload implementation backlog

Status: planned, not a claim of implemented production support.
Requested scope: Windows, macOS, Linux, NAS, virtual machines, Microsoft 365 and Google Workspace.
Visual changes are outside this workstream.

## Current verified boundary

The managed Windows agent, enrollment, protected state, local engine process identity,
configuration revisions, signed commands, restore approval, monitoring and signed
Windows update flow have been implemented and tested. This does not establish
equivalent managed support on Unix or native SaaS/image backup support.

Duplicati storage backends are backup destinations. Supporting S3, WebDAV or SFTP
does not by itself implement backup of an application's API data.

## Implementation order and acceptance criteria

### 1. Linux and macOS managed agents

Ship self-contained packages for linux-x64, linux-arm64, osx-x64 and osx-arm64.
Use systemd on supported Linux distributions and launchd on supported macOS releases.
Validate actual .NET runtime and native engine compatibility for each supported OS.

Keep the Windows process-bound transport. For Unix use a dedicated loopback HTTPS
engine with a locally generated certificate and protected certificate pin; never
send engine credentials to an unverified loopback listener.
Store identity and encryption material in root-owned private directories and reject
symlink or untrusted-owner paths. Do not silently regenerate missing keys for
existing encrypted state. Pass secrets through protected files/environment, not
process arguments. Enrollment must preserve identity across upgrades.

Add authenticated platform reporting to device/agent records. Approved releases,
outdated-agent alerts and deployment selection must match the device platform.
Unix updates need signature, hash, length, archive traversal/link rejection,
compiled version checks and an independent service update supervisor. A helper
inside the systemd service's process group cannot safely stop its own service.

Native CI must exercise enrollment, restart, encrypted state, foreign local engine
rejection, scheduled encrypted backup, byte-identical restore, protected restore
destination, update failure and identity preservation. Build success alone is not
an operational acceptance test. macOS Full Disk Access and Developer ID signing /
notarization need explicit deployment documentation; do not bypass OS protection.

### 2. NAS

First support SMB/NFS shares mounted on a supported agent host. Keep source access
credentials local and minimize their permissions. Define behavior when mounts
disappear so an unavailable source cannot be mistaken for an empty successful
backup.

Native DSM/QTS support requires verified CPU architecture, runtime, service manager
and package compatibility. ARMv7 and arbitrary vendor Linux versions are not
implicitly supported by an ARM64 package. An optional container agent needs private
persistent state, read-only source mounts and explicit container lifecycle updates;
it must not invoke host systemd update routines.

Acceptance: real share backup/restore, disconnection failure, mount recovery and
permission checks; any native vendor package requires tests on that vendor OS.

### 3. Virtual machines and Proxmox containers

Guest agents cover files and applications in supported guest operating systems.
They do not constitute complete bootable VM or LXC image backups.

Full Proxmox VM/LXC protection must use native Proxmox VE / Proxmox Backup Server
operations with typed requests, least-privilege credentials and explicit local
VM/storage allowlists. Do not copy live disk images and claim application
consistency. Model guest file jobs separately from hypervisor image jobs.

Image restore needs a verified archive catalog, native restore implementation,
two-administrator approval, audit events and an explicit new VM/container ID.
Never overwrite an existing guest implicitly. A host spanning customers must not
expose one customer's guest inventory, archives or commands to another.

Acceptance: actual VM and LXC backup, failure reporting, interrupted job handling,
restore into a new ID and boot/data validation on a test Proxmox/PBS installation.
Product UI must distinguish file restore from full guest restore.

### 4. Microsoft 365 and Google Workspace

Use an explicit bring-your-own-license boundary for vendor modules requiring
commercial rights. Before integrating or distributing a module, verify usage and
redistribution rights and preserve its original entitlement checks. Public OSS
agent artifacts must continue excluding restricted assemblies. Having a usage
license does not automatically grant redistribution rights.

Prefer a separately installed, officially licensed engine where appropriate.
Only a locally trusted administrator may select that installation; a remote
configuration must not choose an arbitrary executable. Keep OAuth credentials,
refresh tokens and encryption keys local or in the encrypted secret abstraction.
The console must never display or ingest raw backup passwords.

Document actual supported workloads, API permissions, application consent,
tenant identity, throttling, incremental behavior, deletion/retention semantics
and restore destinations separately for each provider. An API connectivity test
is not a successful backup or restore.

Acceptance: entitlement verified and live test-tenant backup plus restore for
every advertised workload, including pagination, token expiration, throttling
and tenant isolation. Until then, SaaS support must be marked unavailable.

## Release gates

Do not mark any backlog item complete until its native operational tests and
restore checks pass. Do not present synthetic inventory or stub providers as
working backend capabilities. Preserve the existing Windows release and all
license notices. Publish independently reviewable commits and retain an explicit
support matrix as platforms pass acceptance.
