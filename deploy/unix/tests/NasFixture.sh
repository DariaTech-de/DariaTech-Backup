#!/usr/bin/env bash
# Disposable GitHub Linux VM only. A real Samba server and kernel CIFS mount,
# synthetic files and throwaway credentials; never invoked by product install.
set -euo pipefail
umask 077
if [ "$(id -u)" != 0 ] || [ "$(uname -s)" != Linux ]; then exit 2; fi
fixture=/var/lib/dariatech-ci-smb
mountpoint=/mnt/dariatech-ci-smb
case "${1:?start or stop}" in
 start)
  if [ -e "$fixture" ] || mountpoint -q "$mountpoint"; then printf '%s\n' 'Refusing an existing fixture or mount.' >&2; exit 2; fi
  uid="${2:?CI runner UID required}"
  [[ "$uid" =~ ^[0-9]+$ ]] || exit 2
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq samba cifs-utils
  mkdir -m 700 -p "$fixture/share" "$fixture/private" "$fixture/run" "$fixture/log"
  install -d -m 755 "$mountpoint"
  head -c 131072 /dev/urandom > "$fixture/share/restore-check.bin"
  password="$(openssl rand -hex 32)"
  cat > "$fixture/smb.conf" <<EOF
[global]
 interfaces = 127.0.0.1
 bind interfaces only = yes
 smb ports = 1445
 server min protocol = SMB3
 private dir = $fixture/private
 state directory = $fixture/run
 lock directory = $fixture/run
 cache directory = $fixture/run
 pid directory = $fixture/run
 log file = $fixture/log/smbd.log
 log level = 0
 map to guest = Never
[dariatech]
 path = $fixture/share
 read only = yes
 valid users = root
EOF
  printf '%s\n%s\n' "$password" "$password" | smbpasswd -c "$fixture/smb.conf" -s -a root >/dev/null
  printf 'username=root\npassword=%s\n' "$password" > "$fixture/credentials"
  unset password
  smbd -F --no-process-group -s "$fixture/smb.conf" > "$fixture/server.log" 2>&1 &
  printf '%s' "$!" > "$fixture/server.pid"
  for attempt in {1..40}; do
   if mount -t cifs //127.0.0.1/dariatech "$mountpoint" -o "credentials=$fixture/credentials,port=1445,ro,nosuid,nodev,noexec,vers=3.1.1,uid=$uid,file_mode=0600,dir_mode=0700" 2>"$fixture/mount.log"; then break; fi
   sleep 0.25
  done
  if ! mountpoint -q "$mountpoint"; then cat "$fixture/mount.log"; cat "$fixture/server.log"; exit 1; fi
  test -s "$mountpoint/restore-check.bin"
  printf '%s\n' 'PASS: real SMB3 server and kernel CIFS source mounted read-only'
  ;;
 stop)
  if mountpoint -q "$mountpoint"; then umount "$mountpoint"; fi
  if [ -f "$fixture/server.pid" ]; then kill "$(cat "$fixture/server.pid")" 2>/dev/null || true; fi
  rm -rf "$fixture"
  rmdir "$mountpoint" 2>/dev/null || true
  ;;
 *) exit 2 ;;
esac
