namespace DariaTech.Console.Services;

// Local removal of the agent, per platform. The commands use only fixed installation paths and work for every
// agent version, including installations without the installed removal command (agent 0.3.x and older).
public static class AgentRemoval
{
 public sealed record Guide(string Platform,string Title,string Shell,string Gui,string Remove,string Purge,string PurgeRestores,string[] Notes);
 public static readonly string[] Families=["windows","linux","macos"];
 public static string Family(string platform)=>platform.StartsWith("win",StringComparison.Ordinal)?"windows":platform.StartsWith("osx",StringComparison.Ordinal)?"macos":platform.StartsWith("linux",StringComparison.Ordinal)?"linux":"";
 public static Guide For(string family)=>family switch
 {
  "windows"=>new("windows","Windows","PowerShell als Administrator",
   "Einstellungen → Apps → Installierte Apps → „DariaTech Backup“ → Deinstallieren.",
   """
   $u="$env:ProgramFiles\DariaTech Backup\unins000.exe"
   if(Test-Path $u){$p=Start-Process $u -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru;"Deinstallation beendet (Code $($p.ExitCode))"}else{"Agent ist nicht installiert."}
   if(Get-Service DariaTechBackupAgent -ErrorAction SilentlyContinue){"Dienst besteht noch – bitte melden."}else{"Dienst entfernt."}
   """,
   """
   # Löscht Geräte-Identität, Schlüssel, lokale Job-Datenbanken und Konfiguration; wiederhergestellte Dateien bleiben.
   $s="$env:ProgramData\DariaTechBackup"
   if(Test-Path $s){Get-ChildItem $s -Force | Where-Object Name -ne 'Restores' | Remove-Item -Recurse -Force}
   """,
   """
   Remove-Item "$env:ProgramData\DariaTechBackup" -Recurse -Force
   """,
   ["Der Deinstaller entfernt Dienst, Programm und Backup-Engine. Lokale Daten unter C:\\ProgramData\\DariaTechBackup bleiben zunächst erhalten (für eine Neuinstallation desselben PCs).",
    "Wiederhergestellte Dateien liegen in C:\\ProgramData\\DariaTechBackup\\Restores; sie werden nur mit dem letzten Befehl gelöscht."]),
  "linux"=>new("linux","Linux","Terminal, Benutzer mit sudo-Rechten","",
   """
   if [ -x /usr/local/sbin/dariatech-agent-uninstall ]; then sudo /usr/local/sbin/dariatech-agent-uninstall; else
    sudo systemctl disable --now DariaTechBackupAgent.service; sudo rm -f /etc/systemd/system/DariaTechBackupAgent.service
    sudo systemctl daemon-reload; sudo rm -rf /opt/dariatech-backup-agent /opt/dariatech-backup-agent.previous; fi
   """,
   """
   # Löscht Geräte-Identität, Schlüssel, lokale Job-Datenbanken und Konfiguration; wiederhergestellte Dateien bleiben.
   sudo rm -rf /var/lib/dariatech-backup /etc/dariatech-backup
   """,
   """
   sudo rm -rf /var/lib/dariatech-backup-restores
   """,
   ["Wurde der Agent ab Version 0.4 installiert, gibt es zusätzlich den Befehl sudo /usr/local/sbin/dariatech-agent-uninstall (mit --purge bzw. --purge-restores löscht er auch die lokalen Daten). Der Befehl oben funktioniert auch für ältere Installationen.",
    "Wiederhergestellte Dateien liegen in /var/lib/dariatech-backup-restores."]),
  "macos"=>new("macos","macOS","Terminal, Benutzer mit Administratorrechten (sudo)","",
   """
   if [ -x /usr/local/sbin/dariatech-agent-uninstall ]; then sudo /usr/local/sbin/dariatech-agent-uninstall; else
    sudo launchctl bootout system/de.dariatech.dariatechbackupagent 2>/dev/null; sudo rm -f /Library/LaunchDaemons/de.dariatech.dariatechbackupagent.plist
    sudo rm -rf "/Library/Application Support/DariaTechBackup/agent" "/Library/Application Support/DariaTechBackup/agent.previous"; fi
   """,
   """
   # Löscht Geräte-Identität, Schlüssel, lokale Job-Datenbanken, Konfiguration und Protokoll; wiederhergestellte Dateien bleiben.
   sudo rm -rf "/Library/Application Support/DariaTechBackup/state" "/Library/Application Support/DariaTechBackup/config"
   """,
   """
   sudo rm -rf "/Library/Application Support/DariaTechBackup"
   """,
   ["Danach unter Systemeinstellungen → Datenschutz & Sicherheit → Festplattenvollzugriff den Eintrag „DariaTech.Agent“ auswählen und mit „−“ entfernen.",
    "Wurde der Agent ab Version 0.4 installiert, gibt es zusätzlich den Befehl sudo /usr/local/sbin/dariatech-agent-uninstall (mit --purge bzw. --purge-restores löscht er auch die lokalen Daten).",
    "Wiederhergestellte Dateien liegen in /Library/Application Support/DariaTechBackup/Restores."]),
  _=>throw new ArgumentOutOfRangeException(nameof(family))
 };
}
