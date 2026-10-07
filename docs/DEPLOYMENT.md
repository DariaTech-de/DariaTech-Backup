# Console deployment

This repository prepares hosting for https://backup.dariatech.de; it does not create DNS, public infrastructure or accounts automatically. Required: Docker/Compose, PostgreSQL storage backup, protected external key backup, domain routing and trusted reverse proxy.

## Install directly from GitHub (Proxmox LXC)

For a pilot, use an unprivileged Ubuntu 24.04 LXC with nesting/keyctl enabled, a static IP, 2–4 CPU cores, 4 GB RAM and 40 GB disk. Install Docker Engine and the Compose plugin using Docker's official Ubuntu instructions. Docker-in-LXC compatibility depends on the Proxmox kernel/storage; use a VM if the runtime requires weakening container isolation. Do not install the stack on the Proxmox host itself.

Run inside the LXC after installing Docker, Git and Python 3:

```sh
git clone https://github.com/DariaTech-de/DariaTech-Backup.git /opt/dariatech-backup
cd /opt/dariatech-backup
git rev-parse HEAD
```

Record the commit used for deployment. The following instructions build images locally from that checked-out source; a registry or GitHub Release binary is not required. This is the first management slice, not a completed production release; see IMPLEMENTATION_PLAN.md for outstanding features and release gates.

## Initial deployment

```sh
cp .env.example .env
python3 scripts/init-deployment-secrets.py
docker compose build console postgres
# Start the database before making any schema changes.
docker compose up -d postgres
docker compose --profile maintenance run --rm migrate
docker compose up -d console proxy
```

The secret generator refuses to overwrite `.secrets/`. Its containing directory is 0700; single-file mounts are readable by container service UIDs. Never mount the whole host secret directory into unrelated containers. Replace file-backed Compose secrets with your secret manager for production operations. `.env` contains non-secret settings only. `.secrets`, local keys and build artifacts are ignored by Git.

PostgreSQL creates a non-superuser `dariatech_app` role; only the maintenance container mounts the database-owner connection. The running console verifies migrations at startup and fails if any are pending. A new PostgreSQL volume runs the initialization script once; changing secret files does not rotate existing DB passwords. Rotate deliberately using the owner account and update secret references together.

## First administrator

There is no default account or password. Prepare a strong 14+ character password in a protected temporary file and mount it into a one-off console container. Run the published application with `--bootstrap-user` and these settings:

```text
Provision__Email=<operator email>
Provision__Role=SuperAdmin
Provision__PasswordFile=<mounted password-file path>
Provision__TotpOutputFile=<writable protected provisioning-file path>
Database__ConnectionFile=/run/secrets/app_connection
Security__MasterKeyFile=/run/secrets/master_key
Security__KeyDirectory=/var/lib/dariatech/keys
```

Provisioning creates the account, hashes its password, encrypts its TOTP secret and writes the secret once to the requested protected output file. Register it in an authenticator, then securely remove the password/provisioning files. Run under the same UID as the console so key-volume permissions match. Customer roles require Provision__TenantId; operator roles must not have a tenant. No seed/demo customer data is created in production.

For a root-operated LXC shell, this example provisions the first administrator under the image's application UID. The protected directory must not already exist:

```bash
APP_UID=$(docker run --rm --entrypoint id dariatech-backup-console:local -u app)
mkdir -m 700 .provision
read -r -p "Administrator email: " ADMIN_EMAIL
read -r -s -p "Password (14+ characters): " ADMIN_PASSWORD
printf '\n'
printf '%s' "$ADMIN_PASSWORD" > .provision/password
unset ADMIN_PASSWORD
chmod 600 .provision/password
chown -R "$APP_UID" .provision

docker compose run --rm --no-deps \
  -v "$PWD/.provision:/provision" \
  -e Provision__Email="$ADMIN_EMAIL" \
  -e Provision__Role=SuperAdmin \
  -e Provision__PasswordFile=/provision/password \
  -e Provision__TotpOutputFile=/provision/totp.txt \
  console --bootstrap-user

# Read locally, enter in your authenticator, then remove both files.
cat .provision/totp.txt
rm -f .provision/password .provision/totp.txt
rmdir .provision
unset ADMIN_EMAIL
```

## Proxy and domain

Caddy listens on 80/443. Point DNS at the host and allow certificate issuance. Console trusts only the fixed Caddy address 172.30.83.10; adjust subnet/TrustedProxies if that subnet conflicts with your environment. PostgreSQL has no published port. Cloudflare Tunnel can replace Caddy: connect it to the private edge network at an explicitly trusted address and set the forwarded HTTPS scheme; retain public TLS. Never clear the KnownProxies allowlist to trust arbitrary senders.

## Operations

- `/health/live` is process liveness; `/health/ready` checks PostgreSQL access. Health endpoints contain no tenant data. Console container runs as a non-root user with a read-only root filesystem, dropped capabilities and a writable dedicated key volume.
- Persist postgres-data, console-keys and Caddy certificate/config volumes. Back up PostgreSQL AND the encryption master key using separate protected storage. Verify restoration in an isolated environment; losing the master key makes encrypted secrets unreadable.
- Monitoring configuration: OfflineMinutes (default 30), BackupAgeHours (24), PollSeconds (60), LatestApprovedVersion (unset by default). A version entry only drives monitoring; it does not enable an updater.
- SMTP/notifications and automatic configuration/update distribution are not implemented. Do not assume alerts send email.
- For updates: back up DB/key volumes, review migration SQL, run explicit migrate, deploy image, verify readiness and rollback plan. Never start an older binary against an incompatible migrated schema.
- Build environment proxy CAs can be mounted with `docker build --secret id=proxy_bundle,src=/etc/ssl/certs/ca-certificates.crt -f DariaTech/Console/Dockerfile .`; retain TLS verification. The CA mount is not persisted in image layers.

Pin final release images by digest and scan dependencies/images before distribution; versioned tags alone are not an immutable release artifact.

## Update from GitHub

Back up the database, keys and secret files first. Review changes and migrations before selecting the new commit. Do not rerun the secret generator for an existing installation.

```sh
cd /opt/dariatech-backup
git pull --ff-only origin master
docker compose build console postgres
docker compose stop console
docker compose --profile maintenance run --rm migrate
docker compose up -d console proxy
docker compose ps
```

If migration fails, leave the console stopped and diagnose the failure. PostgreSQL major-version upgrades require a separate database upgrade procedure; rebuilding an image alone is not a database upgrade.
