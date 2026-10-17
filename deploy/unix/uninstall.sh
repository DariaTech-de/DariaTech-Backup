#!/usr/bin/env bash
# Removes the DariaTech Backup Agent service and program files from this Linux or macOS device.
#   uninstall.sh                     service and program files; protected state, keys and job databases stay
#   uninstall.sh --purge             additionally deletes state, keys, local job databases and configuration
#   uninstall.sh --purge-restores    additionally deletes the folder with files restored from the Console
# Backups at the storage destination are never touched. Installed as /usr/local/sbin/dariatech-agent-uninstall.
set -euo pipefail
purge=0;restores=0
for arg in "$@"; do
 case "$arg" in
  --purge) purge=1 ;;
  --purge-restores) purge=1;restores=1 ;;
  *) printf '%s\n' "Unbekannte Option: $arg (erlaubt: --purge, --purge-restores)" >&2; exit 2 ;;
 esac
done
if [ "$(id -u)" != 0 ]; then printf '%s\n' 'Bitte mit sudo ausführen.' >&2; exit 2; fi
case "$(uname -s)" in
 Linux)
  prefix=/opt/dariatech-backup-agent
  state=/var/lib/dariatech-backup
  config=/etc/dariatech-backup
  restore_root=/var/lib/dariatech-backup-restores
  service=DariaTechBackupAgent ;;
 Darwin)
  base='/Library/Application Support/DariaTechBackup'
  prefix="$base/agent"
  state="$base/state"
  config="$base/config"
  restore_root="$base/Restores"
  label=de.dariatech.dariatechbackupagent ;;
 *) printf '%s\n' 'Nicht unterstütztes Betriebssystem.' >&2; exit 2 ;;
esac
for path in "$prefix" "$prefix.previous" "$state" "$config" "$restore_root"; do
 if [ -L "$path" ]; then printf '%s\n' "Unsicherer Pfad (symbolischer Link): $path" >&2; exit 2; fi
done
# Prefer the names the installed binary reports; fall back to the defaults when it is already missing or broken.
if [ -x "$prefix/DariaTech.Agent" ]; then
 if [ "$(uname -s)" = Linux ]; then
  reported="$("$prefix/DariaTech.Agent" --service-name 2>/dev/null || true)"
  if [[ "$reported" =~ ^[A-Za-z][A-Za-z0-9]{1,60}$ ]]; then service="$reported"; fi
 else
  reported="$("$prefix/DariaTech.Agent" --service-label 2>/dev/null || true)"
  if [[ "$reported" =~ ^de\.dariatech\.[a-z0-9]+$ ]]; then label="$reported"; fi
 fi
fi
if [ "$(uname -s)" = Linux ]; then
 systemctl disable --now "$service.service" 2>/dev/null || true
 rm -f "/etc/systemd/system/$service.service"
 systemctl daemon-reload
 systemctl reset-failed "$service.service" 2>/dev/null || true
else
 "$prefix/DariaTech.Agent" --service-stop 2>/dev/null || launchctl bootout "system/$label" 2>/dev/null || true
 rm -f "/Library/LaunchDaemons/$label.plist"
fi
# The engine runs as a child of the service; make sure no copy outlives the service.
pkill -f "$prefix/engine/Duplicati.Server" 2>/dev/null || true
rm -rf "$prefix" "$prefix.previous"
rm -f /usr/local/sbin/dariatech-agent-uninstall
printf '%s\n' '✔ Dienst und Programmdateien des DariaTech Backup Agent wurden entfernt.'
if [ "$purge" = 1 ]; then
 rm -rf "$state" "$config"
 printf '%s\n' '✔ Lokale Daten gelöscht: Geräte-Identität, Schlüssel, lokale Job-Datenbanken und Konfiguration.'
 if [ "$restores" = 1 ]; then rm -rf "$restore_root";printf '%s\n' '✔ Ordner mit wiederhergestellten Dateien gelöscht.'
 elif [ -d "$restore_root" ]; then printf '%s\n' "  Wiederhergestellte Dateien bleiben erhalten: $restore_root (löschen mit --purge-restores)."; fi
 if [ "$(uname -s)" = Darwin ]; then rmdir "$base" 2>/dev/null || true; fi
else
 printf '%s\n' "  Geräte-Identität, Schlüssel und lokale Job-Datenbanken bleiben in $state erhalten (vollständig löschen mit --purge)."
fi
printf '%s\n' '  Die Backups im Speicherziel bleiben unverändert und lassen sich über die Console auf einem anderen Gerät wiederherstellen.'
if [ "$(uname -s)" = Darwin ]; then
 printf '%s\n' '  macOS: den Eintrag „DariaTech.Agent“ unter Systemeinstellungen → Datenschutz & Sicherheit → Festplattenvollzugriff mit „−“ entfernen.'
fi
