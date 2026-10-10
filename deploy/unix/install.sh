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
 "$stage/DariaTech.Agent" --service-stop
fi
previous="$prefix.previous"
if [ -L "$previous" ]; then printf '%s\n' 'Unsafe previous installation path.' >&2; exit 2; fi
rm -rf "$previous"
if [ -d "$prefix" ]; then mv "$prefix" "$previous"; fi
mv "$stage" "$prefix"
if ! "$prefix/DariaTech.Agent" "${initialize[@]}"; then
 rm -rf "$prefix"
 if [ -d "$previous" ]; then mv "$previous" "$prefix"; fi
 if [[ "$expected" == linux-* ]]; then systemctl start "$service.service" 2>/dev/null || true; else "$prefix/DariaTech.Agent" --service-start 2>/dev/null || true; fi
 printf '%s\n' '✖ Installation fehlgeschlagen; die vorherige Installation wurde wiederhergestellt.' >&2
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
 "$prefix/DariaTech.Agent" --service-start
 launchctl print "system/$label" >/dev/null
fi
if ! "$prefix/DariaTech.Agent" --service-health "$config"; then
 printf '\n%s\n%s\n%s\n' \
  '✖ Der Agent ist installiert, hat sich aber noch nicht bei der DariaTech Console gemeldet.' \
  '  Häufige Ursachen: Einmal-Code abgelaufen (in der Console einen neuen Befehl erzeugen), keine Internetverbindung oder Firewall.' \
  "  Protokoll: $(if [[ "$expected" == osx-* ]]; then printf '%s' "sudo tail -n 50 '/Library/Application Support/DariaTechBackup/state/agent-service.log'"; else printf '%s' "sudo journalctl -u $service.service -n 50"; fi)" >&2
 exit 2
fi
printf '\n%s\n%s\n' \
 '✔ Installation erfolgreich: Der DariaTech Backup Agent läuft als Hintergrunddienst und ist mit der Console verbunden.' \
 '  Das Gerät erscheint jetzt in der DariaTech Console beim Kunden. Ein Programmfenster gibt es nicht – alles wird über die Console gesteuert.'
if [[ "$expected" == osx-* ]]; then
 # macOS protects Documents, Desktop, Downloads etc. even from root; only the user can grant Full Disk Access.
 printf '\n%s\n%s\n%s\n%s\n%s\n' \
  '➜ Letzter Schritt auf dem Mac: Festplattenvollzugriff erlauben (sonst können Dokumente, Schreibtisch und Downloads nicht gesichert werden).' \
  '  1. Die Systemeinstellungen öffnen sich bei „Datenschutz & Sicherheit → Festplattenvollzugriff“.' \
  '  2. Im geöffneten Finder-Fenster ist „DariaTech.Agent“ markiert: diese Datei in die Liste ziehen (oder „+“ und ⇧⌘G, der Pfad ist schon in der Zwischenablage).' \
  '  3. Den Schalter bei „DariaTech.Agent“ einschalten.' \
  "  Pfad: $prefix/DariaTech.Agent"
 if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != root ]; then
  printf '%s' "$prefix/DariaTech.Agent" | sudo -u "$SUDO_USER" pbcopy 2>/dev/null || true
  sudo -u "$SUDO_USER" open -R "$prefix/DariaTech.Agent" 2>/dev/null || true
  sudo -u "$SUDO_USER" open 'x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles' 2>/dev/null || true
 fi
fi
