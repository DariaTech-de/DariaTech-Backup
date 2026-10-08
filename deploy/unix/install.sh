#!/usr/bin/env bash
set -euo pipefail
umask 077
if [ "$(id -u)" != 0 ]; then printf '%s\n' 'Run the service installer as root.' >&2; exit 2; fi
package_dir="$(cd "$(dirname "$0")" && pwd -P)"
platform="$(cat "$package_dir/platform.txt")"
case "$(uname -s):$(uname -m)" in
 Linux:x86_64) expected=linux-x64 ;;
 Linux:aarch64|Linux:arm64) expected=linux-arm64 ;;
 Darwin:x86_64) expected=osx-x64 ;;
 Darwin:arm64) expected=osx-arm64 ;;
 *) printf '%s\n' 'Unsupported OS or CPU architecture.' >&2; exit 2 ;;
esac
if [ "$platform" != "$expected" ]; then printf '%s\n' 'Package architecture does not match this host.' >&2; exit 2; fi
if [[ "$expected" == linux-* ]]; then
 command -v systemctl >/dev/null
 getconf GNU_LIBC_VERSION >/dev/null
 prefix=/opt/dariatech-backup-agent
 config=/etc/dariatech-backup/agent.json
else
 prefix='/Library/Application Support/DariaTechBackup/agent'
 config='/Library/Application Support/DariaTechBackup/config/agent.json'
fi
# Enrollment and trust are local administrator decisions; config remains outside binaries.
initialize=(--initialize --agent-config "$config")
initialize+=("$@")
parent="$(dirname "$prefix")"
mkdir -p "$parent"
if [ -L "$prefix" ] || [ -L "$parent" ]; then printf '%s\n' 'Installation paths cannot be links.' >&2; exit 2; fi
stage="$(mktemp -d "$parent/.dariatech-install.XXXXXXXX")"
trap 'rm -rf "$stage"' EXIT
cp -R "$package_dir/agent/." "$stage/"
chown -R 0:0 "$stage"
chmod 755 "$stage"
# The native validator checks all assemblies, ownership and ancestry before any service stop.
"$stage/DariaTech.Agent" --check-engine-installation "$stage/engine/Duplicati.Server"
service="$("$stage/DariaTech.Agent" --service-name)"
label="$("$stage/DariaTech.Agent" --service-label)"
if [[ ! "$service" =~ ^[A-Za-z][A-Za-z0-9]{1,60}$ ]] || [[ ! "$label" =~ ^de\.dariatech\.[a-z0-9]+$ ]]; then exit 2; fi
if [[ "$expected" == linux-* ]]; then
 systemctl stop "$service.service" 2>/dev/null || true
else
 launchctl bootout "system/$label" 2>/dev/null || true
fi
previous="$prefix.previous"
if [ -L "$previous" ]; then printf '%s\n' 'Unsafe previous installation path.' >&2; exit 2; fi
rm -rf "$previous"
if [ -d "$prefix" ]; then mv "$prefix" "$previous"; fi
mv "$stage" "$prefix"
if ! "$prefix/DariaTech.Agent" "${initialize[@]}"; then
 rm -rf "$prefix"
 if [ -d "$previous" ]; then mv "$previous" "$prefix"; fi
 if [[ "$expected" == linux-* ]]; then systemctl start "$service.service" 2>/dev/null || true; else launchctl bootstrap system "/Library/LaunchDaemons/$label.plist" 2>/dev/null || true; fi
 printf '%s\n' 'Initialization failed; the previous installation was restored.' >&2
 exit 2
fi
# Existing configuration is retained by initialization, including pins and identity.
if [[ "$expected" == linux-* ]]; then
 "$prefix/DariaTech.Agent" --write-service-file "$config"
 systemctl daemon-reload
 systemctl enable --now "$service.service"
 systemctl is-active --quiet "$service.service"
else
 plist="/Library/LaunchDaemons/$label.plist"
 "$prefix/DariaTech.Agent" --write-service-file "$config"
 launchctl bootstrap system "$plist"
 launchctl print "system/$label" >/dev/null
fi
printf '%s\n' 'DariaTech Backup Agent installed. Enrollment and heartbeats complete after network connectivity is available.'
