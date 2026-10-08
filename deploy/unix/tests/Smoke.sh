#!/usr/bin/env bash
# CI-only HTTPS enrollment fixture; never shipped as a backend implementation.
set -euo pipefail
umask 077
rid="${1:?Native RID required}"
repo="$(cd "$(dirname "$0")/../../.." && pwd -P)"
if [ "$(uname -s)" = Darwin ]; then work="$(mktemp -d /private/tmp/dariatech-ci.XXXXXXXX)"; else work="$(mktemp -d)"; fi
node_pid=''
cleanup() {
 if [ -n "$node_pid" ]; then kill "$node_pid" 2>/dev/null || true; fi
 if [ "$(uname -s)" = Linux ]; then
  systemctl disable --now DariaTechBackupAgent.service 2>/dev/null || true
  rm -f /usr/local/share/ca-certificates/dariatech-ci.crt
  update-ca-certificates >/dev/null 2>&1 || true
 else
  launchctl bootout system/de.dariatech.dariatechbackupagent 2>/dev/null || true
  if [ -f "$work/ca.pem" ]; then security remove-trusted-cert -d "$work/ca.pem" 2>/dev/null || true; fi
 fi
 rm -rf "$work"
}
trap cleanup EXIT
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/ca.key" -out "$work/ca.pem" -days 1 -subj /CN=DariaTech-CI-only-root >/dev/null 2>&1
openssl req -new -newkey rsa:2048 -nodes -keyout "$work/server.key" -out "$work/server.csr" -subj /CN=localhost >/dev/null 2>&1
printf '%s\n' 'subjectAltName=IP:127.0.0.1,DNS:localhost' 'extendedKeyUsage=serverAuth' > "$work/extensions"
openssl x509 -req -in "$work/server.csr" -CA "$work/ca.pem" -CAkey "$work/ca.key" -CAcreateserial -out "$work/server.pem" -days 1 -extfile "$work/extensions" >/dev/null 2>&1
export FIXTURE_PASSWORD="$(openssl rand -hex 32)"
openssl pkcs12 -export -out "$work/server.pfx" -inkey "$work/server.key" -in "$work/server.pem" -certfile "$work/ca.pem" -passout env:FIXTURE_PASSWORD
if [ "$(uname -s)" = Linux ]; then
 install -m 644 "$work/ca.pem" /usr/local/share/ca-certificates/dariatech-ci.crt
 update-ca-certificates >/dev/null
 prefix=/opt/dariatech-backup-agent
 config=/etc/dariatech-backup/agent.json
 state=/var/lib/dariatech-backup
else
 security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$work/ca.pem"
 prefix='/Library/Application Support/DariaTechBackup/agent'
 config='/Library/Application Support/DariaTechBackup/config/agent.json'
 state='/Library/Application Support/DariaTechBackup/state'
fi
export FIXTURE_TOKEN="$(openssl rand -hex 32)"
export FIXTURE_PFX="$work/server.pfx" FIXTURE_RESULT="$work/result.json" FIXTURE_ENGINE_PASSWORD="$(openssl rand -hex 32)"
printf '%s' "$FIXTURE_TOKEN" > "$work/token"
mkdir -p "$state"
chmod 700 "$state"
printf '%s' "$FIXTURE_ENGINE_PASSWORD" > "$state/engine-password.txt"
node "$repo/deploy/unix/tests/console-fixture.cjs" >/dev/null 2>&1 &
node_pid=$!
for attempt in {1..40}; do if curl --silent --fail https://127.0.0.1:18443/health >/dev/null; then break; fi; sleep 0.25; done
bash "$repo/artifacts/unix/$rid/payload/install.sh" --console-url https://127.0.0.1:18443 --enrollment-token-file "$work/token"
for attempt in {1..90}; do
 if [ -f "$work/result.json" ] && python3 -c 'import json,sys;assert json.load(open(sys.argv[1]))["heartbeats"]>0' "$work/result.json" 2>/dev/null; then break; fi
 sleep 1
done
python3 -c 'import json,sys;v=json.load(open(sys.argv[1]));assert v["enrolled"] and v["heartbeats"]>0' "$work/result.json"
test -s "$state/identity.bin"
test ! -e "$state/enrollment-token.txt"
test ! -e "$state/engine-password.txt"
identity_hash="$(openssl dgst -sha256 "$state/identity.bin")"
# Service restart and installer upgrade must preserve the protected enrollment identity.
if [ "$(uname -s)" = Linux ]; then systemctl restart DariaTechBackupAgent.service; else launchctl kickstart -k system/de.dariatech.dariatechbackupagent; fi
bash "$repo/artifacts/unix/$rid/payload/install.sh"
test "$(openssl dgst -sha256 "$state/identity.bin")" = "$identity_hash"
# The token has already been consumed; the fixture refuses any second enrollment.
test "$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["enrollments"])' "$work/result.json")" = 1
bash "$repo/artifacts/unix/$rid/payload/uninstall.sh"
test -s "$state/identity.bin"
test -s "$state/agent.key"
test -s "$config"
printf '%s\n' 'PASS: native service, HTTPS enrollment, heartbeat, restart, identity-preserving upgrade and retained uninstall state'
