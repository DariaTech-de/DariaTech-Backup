#!/usr/bin/env bash
set -euo pipefail
if [ "$(id -u)" != 0 ]; then printf '%s\n' 'Run as root.' >&2; exit 2; fi
case "$(uname -s)" in
 Linux) prefix=/opt/dariatech-backup-agent ;;
 Darwin) prefix='/Library/Application Support/DariaTechBackup/agent' ;;
 *) exit 2 ;;
esac
if [ ! -f "$prefix/service.json" ] || [ -L "$prefix" ]; then printf '%s\n' 'Verified local installation not found.' >&2; exit 2; fi
service="$("$prefix/DariaTech.Agent" --service-name)"
label="$("$prefix/DariaTech.Agent" --service-label)"
if [[ ! "$service" =~ ^[A-Za-z][A-Za-z0-9]{1,60}$ ]] || [[ ! "$label" =~ ^de\.dariatech\.[a-z0-9]+$ ]]; then exit 2; fi
if [ "$(uname -s)" = Linux ]; then
 systemctl disable --now "$service.service"
 rm -f "/etc/systemd/system/$service.service"
 systemctl daemon-reload
else
 launchctl bootout "system/$label"
 rm -f "/Library/LaunchDaemons/$label.plist"
fi
rm -rf "$prefix"
printf '%s\n' 'Agent service removed. Encrypted state, keys and backup databases are retained; revoke the device in the Console.'
