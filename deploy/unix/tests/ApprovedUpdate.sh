#!/usr/bin/env bash
# CI-only native signed update acceptance; never install this fixture in production.
set -euo pipefail
umask 077
rid="${1:?RID required}"
node_bin="${2:?Absolute CI Node executable required}"
: "${UPDATE_CANDIDATE_VERSION:?Candidate agent version required}"
repo="$(cd "$(dirname "$0")/../../.." && pwd -P)"
if [ "$(uname -s)" = Darwin ]; then work="$(mktemp -d /private/tmp/dariatech-update.XXXXXXXX)"; else work="$(mktemp -d)"; fi
node_pid=''
cleanup() {
 if [ -n "$node_pid" ]; then kill "$node_pid" 2>/dev/null || true; fi
 if [ "$(uname -s)" = Linux ]; then systemctl disable --now DariaTechBackupAgent.service 2>/dev/null || true; else launchctl kill SIGTERM system/de.dariatech.dariatechbackupagent 2>/dev/null || true; fi
}
trap cleanup EXIT
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/ca.key" -out "$work/ca.pem" -days 1 -subj /CN=DariaTech-update-CI-only-root >/dev/null 2>&1
openssl req -new -newkey rsa:2048 -nodes -keyout "$work/server.key" -out "$work/server.csr" -subj /CN=localhost >/dev/null 2>&1
printf '%s\n' 'subjectAltName=IP:127.0.0.1,DNS:localhost' 'extendedKeyUsage=serverAuth' > "$work/extensions"
openssl x509 -req -in "$work/server.csr" -CA "$work/ca.pem" -CAkey "$work/ca.key" -CAcreateserial -out "$work/server.pem" -days 1 -extfile "$work/extensions" >/dev/null 2>&1
export FIXTURE_SERVER_KEY="$work/server.key" FIXTURE_SERVER_CERT="$work/server.pem" FIXTURE_CA="$work/ca.pem"
if [ "$(uname -s)" = Linux ]; then
 install -m 644 "$work/ca.pem" /usr/local/share/ca-certificates/dariatech-update-ci.crt
 update-ca-certificates >/dev/null
 prefix=/opt/dariatech-backup-agent
 state=/var/lib/dariatech-backup
 config=/etc/dariatech-backup/agent.json
else
 security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$work/ca.pem"
 prefix='/Library/Application Support/DariaTechBackup/agent'
 state='/Library/Application Support/DariaTechBackup/state'
 config='/Library/Application Support/DariaTechBackup/config/agent.json'
fi
# A separate disposable enrollment belongs to this fixture. Ordinary installer
# identity preservation is tested separately; existing customer state is never reset.
rm -rf "$state" "$(dirname "$config")"
export FIXTURE_TOKEN="$(openssl rand -hex 32)"
export FIXTURE_RESULT="$work/result.json"
export UPDATE_PRIVATE_KEY="$work/offline.pem" UPDATE_PHASE="$work/phase" UPDATE_PLATFORM="$rid"
openssl ecparam -name prime256v1 -genkey -noout -out "$UPDATE_PRIVATE_KEY"
openssl pkey -in "$UPDATE_PRIVATE_KEY" -pubout -out "$work/update.pub"
printf '%s' "$FIXTURE_TOKEN" > "$work/token"
printf '%s' bad > "$UPDATE_PHASE"
export UPDATE_ARTIFACT="$repo/artifacts/update/$rid/DariaTechBackupAgent-$UPDATE_CANDIDATE_VERSION-$rid.tar.gz"
"$node_bin" "$repo/deploy/unix/tests/update-fixture.cjs" >"$work/fixture.log" 2>&1 &
node_pid=$!
for attempt in {1..40}; do
 if ! kill -0 "$node_pid" 2>/dev/null; then cat "$work/fixture.log"; exit 1; fi
 if curl --silent --fail --connect-timeout 2 --max-time 3 https://127.0.0.1:18443/health >/dev/null; then break; fi
 sleep 0.25
done
curl --silent --fail --connect-timeout 2 --max-time 3 https://127.0.0.1:18443/health >/dev/null
bash "$repo/artifacts/unix/$rid/payload/install.sh" --console-url https://127.0.0.1:18443 --enrollment-token-file "$work/token" --update-key-file "$work/update.pub" --allow-agent-updates
python3 - "$config" <<'PY'
import json,sys,os
path=sys.argv[1];data=json.load(open(path));data['Agent']['UpdateDownloadHosts']=['localhost'];data['Agent']['HeartbeatSeconds']=10
with open(path,'w') as file: json.dump(data,file)
os.chmod(path,0o600)
PY
if [ "$(uname -s)" = Linux ]; then systemctl restart DariaTechBackupAgent.service; else launchctl kickstart -k system/de.dariatech.dariatechbackupagent; fi
wait_receipt() {
 for attempt in {1..150}; do
  if [ -f "$FIXTURE_RESULT" ] && python3 -c 'import json,sys;assert any(r["phase"]==sys.argv[2] and r["status"]==sys.argv[3] for r in json.load(open(sys.argv[1]))["receipts"])' "$FIXTURE_RESULT" "$1" "$2" 2>/dev/null; then return; fi
  sleep 1
 done
 if [ "$(uname -s)" = Linux ]; then journalctl -u DariaTechBackupAgent.service -n 80 --no-pager; else tail -n 80 "$state/agent-service.log"; fi
 return 1
}
wait_receipt bad Rejected
identity_hash="$(openssl dgst -sha256 "$state/identity.bin")"
printf '%s' tampered > "$UPDATE_PHASE"
wait_receipt tampered Failed
printf '%s' good > "$UPDATE_PHASE"
wait_receipt good Installed
test "$(openssl dgst -sha256 "$state/identity.bin")" = "$identity_hash"
# The updated agent reports its version with its first heartbeat, which follows the receipt once the
# restarted engine has finished starting; wait for it instead of racing the receipt.
reported=''
for attempt in {1..120}; do
 if python3 -c 'import json,sys;v=json.load(open(sys.argv[1]));assert v["enrollments"]==1 and sys.argv[2]+".0" in v["versions"]' "$FIXTURE_RESULT" "$UPDATE_CANDIDATE_VERSION" 2>/dev/null; then reported=1; break; fi
 sleep 1
done
if [ -z "$reported" ]; then
 if [ "$(uname -s)" = Linux ]; then journalctl -u DariaTechBackupAgent.service -n 80 --no-pager; else tail -n 80 "$state/agent-service.log"; fi
 cat "$FIXTURE_RESULT"; exit 1
fi
test "$("$prefix/DariaTech.Agent" --service-name)" = DariaTechBackupAgent
printf '%s\n' "PASS: independent native supervisor, invalid-signature rejection, tampered-binary rejection, actual update to $UPDATE_CANDIDATE_VERSION and retained identity"
