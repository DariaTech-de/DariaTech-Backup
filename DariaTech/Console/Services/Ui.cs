using System.Globalization;
using DariaTech.Contracts;
using Microsoft.AspNetCore.Html;
namespace DariaTech.Console.Services;

// Presentation helpers for Razor pages. Formatting only; no data access and no inline styles (CSP: style-src 'self').
public static class Ui
{
 static readonly CultureInfo De=CultureInfo.GetCultureInfo("de-DE");

 public static string StatusLabel(string status)=>status switch{"Healthy"=>"Gesund","Warning"=>"Warnung","Critical"=>"Kritisch","Offline"=>"Offline","Absent"=>"Abwesend",_=>"Unbekannt"};
 public static string StatusClass(string status)=>status switch{"Healthy"=>"healthy","Warning"=>"warning","Critical"=>"critical","Offline"=>"offline","Absent"=>"offline",_=>"unknown"};
 public static string RunLabel(RunStatus s)=>s switch{RunStatus.Success=>"Erfolgreich",RunStatus.Warning=>"Warnung",RunStatus.Failed=>"Fehlgeschlagen",RunStatus.Running=>"Läuft",RunStatus.Cancelled=>"Abgebrochen",_=>"Unbekannt"};
 public static string RunClass(RunStatus s)=>s switch{RunStatus.Success=>"healthy",RunStatus.Warning or RunStatus.Cancelled=>"warning",RunStatus.Failed=>"critical",RunStatus.Running=>"running",_=>"unknown"};
 public static string SeverityClass(string severity)=>severity=="Critical"?"critical":"warning";
 public static string SeverityLabel(string severity)=>severity=="Critical"?"Kritisch":"Warnung";
 public static string AlertTitle(string code)=>code switch
 {
  "DeviceOffline"=>"Gerät offline","EngineUnavailable"=>"Backup-Engine nicht erreichbar","NoBackupJobs"=>"Keine Backup-Jobs eingerichtet",
  "BackupOverdue"=>"Backup überfällig","BackupFailed"=>"Backup fehlgeschlagen","EngineOperationFailed"=>"Engine-Vorgang fehlgeschlagen",
  "BackupWarning"=>"Backup mit Warnungen beendet","BackupNotSuccessful"=>"Backup nicht vollständig","AgentOutdated"=>"Agent-Version veraltet",
  "RepeatedBackupFailures"=>"Wiederholt fehlgeschlagene Backups","BackupVerificationFailed"=>"Backup-Prüfung fehlgeschlagen","RetentionFailed"=>"Aufbewahrung fehlgeschlagen",
  "StorageCapacityWarning"=>"Speicher fast voll","StorageCapacityCritical"=>"Speicher kritisch voll",
  "BackupManualOnly"=>"Kein Zeitplan – noch nie gesichert","ConfigurationFailed"=>"Konfiguration auf dem Gerät fehlgeschlagen",
  "BackupDeletionFailed"=>"Gelöschtes Backup: Zeitplan nicht entfernt",
  "ManagedJobMissing"=>"Verwalteter Backup-Job fehlt auf dem Gerät","InventoryIncomplete"=>"Gerät meldet eine unvollständige Job-Liste",
  "HandoverPending"=>"Geräteübernahme wartet auf Stopp des Altgeräts","AgentUpdateFailed"=>"Agent-Update fehlgeschlagen",_=>code
 };
 // The next step for an alert, phrased for the administrator who has to act.
 public static string AlertAction(string code)=>code switch
 {
  "DeviceOffline"=>"Prüfen, ob das Gerät eingeschaltet und im Internet ist. Bei Urlaub/Lager eine Abwesenheit eintragen. Eine aktuelle Sicherung kann trotzdem vorhanden sein.",
  "EngineUnavailable"=>"Agent-Dienst auf dem Gerät neu starten; Protokoll des Agenten prüfen.",
  "NoBackupJobs"=>"Backup-Job für das Gerät anlegen.",
  "BackupOverdue"=>"Letzte Läufe prüfen; ist das Gerät zu den Zeiten aus, Zeitplan anpassen oder Backup manuell starten.",
  "BackupFailed" or "EngineOperationFailed"=>"Fehler im Job-Verlauf öffnen; Speicher-Verbindung vom Gerät testen; danach Backup erneut starten.",
  "RepeatedBackupFailures"=>"Dauerhafter Fehler: Zugangsdaten, Speicherplatz und Quellordner prüfen, Verbindung testen.",
  "BackupWarning"=>"Einzelne Dateien wurden nicht gesichert (z. B. gesperrt oder ohne Leserecht). Quellen und Rechte prüfen.",
  "BackupNotSuccessful"=>"Lauf wurde abgebrochen oder Ergebnis ist unbekannt – Backup erneut starten und Ergebnis prüfen.",
  "StorageCapacityWarning" or "StorageCapacityCritical"=>"Speicher erweitern oder Aufbewahrung verringern; bei vollem Speicher schlagen Sicherungen fehl.",
  "RetentionFailed"=>"Alte Versionen konnten nicht gelöscht werden – Rechte am Speicher prüfen.",
  "BackupVerificationFailed"=>"Backup-Prüfung fehlgeschlagen: Reparatur prüfen und einen Restore-Test durchführen.",
  "AgentOutdated"=>"Agent-Update freigeben.",
  "AgentUpdateFailed"=>"Fehlercode auf der Geräteseite prüfen. Der bisherige Agent läuft weiter und sichert. Danach ein neueres Release freigeben oder den Agenten mit dem aktuellen Befehl aus „Gerät hinzufügen“ neu installieren.",
  "BackupManualOnly"=>"Zeitplan eintragen oder Backup manuell starten.",
  "ConfigurationFailed"=>"Konfiguration öffnen und Fehler beheben; das Gerät hat sie nicht übernommen.",
  "BackupDeletionFailed"=>"Das Gerät hat das Entfernen des Zeitplans nicht übernommen und sichert womöglich weiter. Der Agent versucht es beim nächsten Abgleich erneut; sonst den Agenten aktualisieren oder den Job auf dem Gerät entfernen.",
  "ManagedJobMissing"=>"Der Job wurde auf dem Gerät entfernt. Konfiguration erneut speichern, damit das Gerät ihn wieder anlegt.",
  "InventoryIncomplete"=>"Agent aktualisieren; die Job-Liste des Geräts enthält ungültige Einträge.",
  "HandoverPending"=>"Altes Gerät einschalten, damit es den Stopp bestätigt – oder auf der Geräteseite manuell bestätigen, dass es nie wieder online kommt.",
  _=>"Gerät öffnen und Details prüfen."
 };
 public static string Ownership(string o)=>o=="Local"?"Lokal verwaltet":o=="Managed"?"Zentral verwaltet":o;

 public static string Ago(DateTimeOffset? t)
 {
  if(t is null)return "Noch nie";
  var d=DateTimeOffset.UtcNow-t.Value;
  if(d<TimeSpan.Zero)return In(t);
  if(d.TotalMinutes<1)return "gerade eben";
  if(d.TotalMinutes<60)return $"vor {(int)d.TotalMinutes} Min.";
  if(d.TotalHours<24)return $"vor {(int)d.TotalHours} Std.";
  if(d.TotalDays<2)return "gestern";
  return $"vor {(int)d.TotalDays} Tagen";
 }
 public static string In(DateTimeOffset? t)
 {
  if(t is null)return "Nicht geplant";
  var d=t.Value-DateTimeOffset.UtcNow;
  if(d<=TimeSpan.Zero)return "fällig";
  if(d.TotalMinutes<60)return $"in {Math.Max(1,(int)d.TotalMinutes)} Min.";
  if(d.TotalHours<24)return $"in {(int)d.TotalHours} Std.";
  return $"in {(int)d.TotalDays} Tagen";
 }
 public static string Date(DateTimeOffset? t)=>t?.ToUniversalTime().ToString("dd.MM.yyyy · HH:mm 'UTC'",De)??"Unbekannt";
 public static string Size(long? v)
 {
  if(v is not {} b)return "—";
  string[] units=["B","KB","MB","GB","TB","PB"];double x=b;var i=0;
  while(x>=1024&&i<units.Length-1){x/=1024;i++;}
  return x.ToString(i==0?"0":x<10?"0.0#":"0.#",De)+" "+units[i];
 }
 // Schedules are entered and shown in German local time; the engine keeps the wall-clock time across daylight
 // saving changes in the device's own time zone.
 public static readonly TimeZoneInfo Zone=FindZone();
 static TimeZoneInfo FindZone(){try{return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");}catch(Exception ex)when(ex is TimeZoneNotFoundException or InvalidTimeZoneException){return TimeZoneInfo.Utc;}}
 public static DateTime ToLocal(DateTimeOffset t)=>TimeZoneInfo.ConvertTime(t,Zone).DateTime;
 public static DateTimeOffset FromLocal(DateTime local)
 {
  var t=DateTime.SpecifyKind(local,DateTimeKind.Unspecified);
  if(Zone.IsInvalidTime(t))t=t.AddHours(1); // 02:30 on the spring-forward day does not exist; use 03:30
  return new DateTimeOffset(t,Zone.GetUtcOffset(t)).ToUniversalTime();
 }
 public static string LocalDate(DateTimeOffset? t)=>t is {} x?TimeZoneInfo.ConvertTime(x,Zone).ToString("dd.MM.yyyy · HH:mm 'Uhr'",De):"Unbekannt";
 // Retention keeps a number of versions, not days: how many days that covers depends on the schedule.
 public static string Retention(int versions,int? repeatHours,int days)
 {
  if(versions<1)return "—";
  if(repeatHours is not int repeat)return $"{versions} Versionen (manuell – entspricht keiner festen Zeitspanne)";
  var runsPerWeek=(days is 0 or 127?7:System.Numerics.BitOperations.PopCount((uint)days))*Math.Max(1d,24d/repeat)*(repeat>24?24d/repeat:1);
  var covered=versions/runsPerWeek*7;
  return $"{versions} Versionen ≈ {(covered>=60?Math.Round(covered/30.4).ToString(De)+" Monate":Math.Round(covered).ToString(De)+" Tage")} bei diesem Zeitplan";
 }
 // "Saved" is not "applied": the distribution state of a centrally managed job on its device.
 public static (string Label,string Class) ConfigState(DariaTech.Console.Data.ManagedJob m,bool handoverPending)=>
  handoverPending?("Wartet auf Stopp des Altgeräts","warning")
  :m.Status is "Failed" or "Rejected"?($"Auf dem Gerät fehlgeschlagen (Revision {m.LatestRevision})","critical")
  :m.AppliedRevision==0?("Gespeichert – noch nicht auf dem Gerät eingerichtet","warning")
  :m.AppliedRevision<m.LatestRevision?($"Änderung gespeichert – Gerät nutzt noch Revision {m.AppliedRevision} von {m.LatestRevision}","warning")
  :($"Auf dem Gerät angewendet (Revision {m.AppliedRevision})","healthy");
 public static string Number(long v)=>v.ToString("N0",De);
 public static string Percent(double v)=>v.ToString("0.0",De);
 public static string Duration(TimeSpan? d)=>d is not {} x?"—":x.TotalHours>=1?$"{(int)x.TotalHours} Std. {x.Minutes} Min.":x.TotalMinutes>=1?$"{x.Minutes} Min. {x.Seconds} Sek.":$"{Math.Max(0,x.Seconds)} Sek.";
 public static string Initials(string? name)
 {
  var local=(name??"").Split('@')[0];var parts=local.Split(['.','-','_',' '],StringSplitOptions.RemoveEmptyEntries);
  return parts.Length switch{0=>"?",1=>parts[0][..Math.Min(2,parts[0].Length)].ToUpperInvariant(),_=>(parts[0][..1]+parts[^1][..1]).ToUpperInvariant()};
 }

 static readonly Dictionary<string,string> Paths=new()
 {
  ["dashboard"]="<rect x=\"3\" y=\"3\" width=\"7\" height=\"9\" rx=\"1.5\"/><rect x=\"14\" y=\"3\" width=\"7\" height=\"5\" rx=\"1.5\"/><rect x=\"14\" y=\"12\" width=\"7\" height=\"9\" rx=\"1.5\"/><rect x=\"3\" y=\"16\" width=\"7\" height=\"5\" rx=\"1.5\"/>",
  ["customers"]="<path d=\"M3 21h18\"/><path d=\"M5 21V7l7-4 7 4v14\"/><path d=\"M9 21v-6h6v6\"/>",
  ["device"]="<rect x=\"3\" y=\"4\" width=\"18\" height=\"12\" rx=\"2\"/><path d=\"M8 20h8M12 16v4\"/>",
  ["server"]="<rect x=\"3\" y=\"3\" width=\"18\" height=\"7\" rx=\"2\"/><rect x=\"3\" y=\"14\" width=\"18\" height=\"7\" rx=\"2\"/><path d=\"M7 6.5h.01M7 17.5h.01M11 6.5h6M11 17.5h6\"/>",
  ["backups"]="<ellipse cx=\"12\" cy=\"5\" rx=\"8\" ry=\"3\"/><path d=\"M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5\"/><path d=\"M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6\"/>",
  ["alert"]="<path d=\"M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z\"/><path d=\"M12 9v4M12 17h.01\"/>",
  ["activity"]="<path d=\"M22 12h-4l-3 9L9 3l-3 9H2\"/>",
  ["users"]="<circle cx=\"9\" cy=\"8\" r=\"4\"/><path d=\"M2 21c0-3.9 3.1-7 7-7s7 3.1 7 7\"/><path d=\"M17 4a4 4 0 0 1 0 8M22 21c0-3-1.8-5.5-4.5-6.5\"/>",
  ["settings"]="<circle cx=\"12\" cy=\"12\" r=\"3\"/><path d=\"M12 2v3M12 19v3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M2 12h3M19 12h3M4.9 19.1 7 17M17 7l2.1-2.1\"/>",
  ["license"]="<path d=\"M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z\"/><path d=\"M14 3v6h6M8 13h8M8 17h5\"/>",
  ["search"]="<circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m21 21-4.3-4.3\"/>",
  ["bell"]="<path d=\"M18 8a6 6 0 1 0-12 0c0 7-3 9-3 9h18s-3-2-3-9\"/><path d=\"M13.7 21a2 2 0 0 1-3.4 0\"/>",
  ["moon"]="<path d=\"M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z\"/>",
  ["logout"]="<path d=\"M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4\"/><path d=\"m16 17 5-5-5-5M21 12H9\"/>",
  ["plus"]="<path d=\"M12 5v14M5 12h14\"/>",
  ["download"]="<path d=\"M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3\"/>",
  ["arrow"]="<path d=\"M5 12h14M13 6l6 6-6 6\"/>",
  ["lock"]="<rect x=\"5\" y=\"11\" width=\"14\" height=\"10\" rx=\"2\"/><path d=\"M8 11V7a4 4 0 0 1 8 0v4\"/>",
  ["shield"]="<path d=\"M12 3l8 3v6c0 4.5-3.4 8.4-8 9-4.6-.6-8-4.5-8-9V6z\"/><path d=\"m9 12 2 2 4-4\"/>",
  ["info"]="<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 8h.01M11 12h1v4h1\"/>",
  ["check"]="<path d=\"m5 12 5 5L20 7\"/>",
  ["cloud"]="<path d=\"M17.5 19a4.5 4.5 0 1 0 0-9h-1.3A7 7 0 1 0 4 15.3\"/>",
  ["phone"]="<path d=\"M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1.9.4 1.8.7 2.7a2 2 0 0 1-.5 2.1L8 9.8a16 16 0 0 0 6 6l1.3-1.3a2 2 0 0 1 2.1-.4c.9.3 1.8.6 2.7.7a2 2 0 0 1 1.7 2z\"/>",
  ["mail"]="<rect x=\"3\" y=\"5\" width=\"18\" height=\"14\" rx=\"2\"/><path d=\"m3 7 9 6 9-6\"/>",
  ["pin"]="<path d=\"M12 21s-7-6.2-7-11a7 7 0 0 1 14 0c0 4.8-7 11-7 11z\"/><circle cx=\"12\" cy=\"10\" r=\"2.5\"/>",
  ["key"]="<circle cx=\"7.5\" cy=\"15.5\" r=\"4.5\"/><path d=\"m10.7 12.3 9.3-9.3M17 6l3 3M14 9l2 2\"/>",
 };
 public static IHtmlContent Icon(string name,string? cls=null)=>new HtmlString($"<svg class=\"icon{(cls is null?"":" "+cls)}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">{Paths[name]}</svg>");
 public static readonly IHtmlContent Mark=new HtmlString("<svg class=\"mark\" viewBox=\"11.215 1.256 4.2 4.2\" aria-hidden=\"true\" focusable=\"false\"><g transform=\"matrix(0.765,0,0,0.765,2.9,2.2)\"><path d=\"M14.127-.3 13.61-.817 11.283 1.509 13.61 3.836 14.127 3.319 12.317 1.509Z\"/><path d=\"M14.644.217 15.936 1.509 14.644 2.802 13.351 1.509Z\"/></g></svg>");
}
