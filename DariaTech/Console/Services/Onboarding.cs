using DariaTech.Console.Data;
using DariaTech.Contracts;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Services;

// Protection state of a device, step by step from registration to a checked restore. Every step except the
// last is technical evidence from the device; "Restore geprüft" additionally needs an administrator to confirm
// that restored files are usable, which is recorded as a manual statement.
public enum Step { Open, Running, Done, Failed }
public sealed record DeviceProtection(Device Device,bool Connected,bool EngineReady,Step Configuration,bool StorageReached,Step FirstBackup,
 Step RestoreTest,string? RestoreCheckedBy,DateTimeOffset? NextRun,bool ManualOnly,DateTimeOffset? LastSuccess)
{
 // Accepted: everything technically proven and a restore confirmed as usable.
 public bool Accepted=>EngineReady&&Configuration==Step.Done&&StorageReached&&FirstBackup==Step.Done&&RestoreTest==Step.Done&&(NextRun is not null||ManualOnly);
 public string NextAction=>
  !Connected?"Agent installieren bzw. Gerät einschalten":
  !EngineReady?"Backup-Engine auf dem Gerät prüfen":
  Configuration==Step.Open?"Backup-Job anlegen bzw. Übernahme abwarten":
  Configuration==Step.Failed?"Fehlgeschlagene Konfiguration prüfen":
  Configuration==Step.Running?"Warten, bis das Gerät die Konfiguration übernimmt":
  !StorageReached?"Verbindung zum Speicher vom Gerät aus testen":
  FirstBackup==Step.Failed?"Fehlgeschlagene Erstsicherung prüfen":
  FirstBackup==Step.Running?"Erstsicherung läuft":
  FirstBackup==Step.Open?"Erstsicherung starten":
  RestoreTest==Step.Open?"Beispieldatei in neuen Ordner wiederherstellen":
  RestoreTest==Step.Running?"Wiederhergestellte Datei öffnen und als nutzbar bestätigen":
  RestoreTest==Step.Failed?"Fehlgeschlagene Wiederherstellung prüfen":
  NextRun is null&&!ManualOnly?"Zeitplan prüfen – kein nächster Lauf bekannt":"Keine – abgenommen";
}
public sealed record CustomerSetup(int Expected,int Registered,int PendingTokens,int Connected,int Configured,int BackedUp,int RestoreChecked,List<DeviceProtection> Devices)
{
 // "Regulärer Betrieb" only when every expected device is registered and accepted; until then "In Einrichtung".
 public bool Operational=>Registered>=Expected&&Expected>0&&Devices.All(x=>x.Accepted);
 public string Summary=>$"{Expected} {(Expected==1?"Gerät":"Geräte")} vorgesehen, {Connected} verbunden, {Configured} konfiguriert, {BackedUp} erfolgreich gesichert, {RestoreChecked} mit geprüftem Restore";
}
public static class Onboarding
{
 public static async Task<CustomerSetup> Load(ManagementDb db,Customer customer,MonitoringOptions o,CancellationToken ct=default)
 {
  var tenant=customer.TenantId;var now=DateTimeOffset.UtcNow;
  var devices=await db.Devices.Where(x=>x.TenantId==tenant&&x.Active).OrderBy(x=>x.Name).ToListAsync(ct);
  var managed=await db.ManagedJobs.Where(x=>x.TenantId==tenant).ToListAsync(ct);
  var jobs=await db.Jobs.Where(x=>x.TenantId==tenant&&x.Active).ToListAsync(ct);
  var facts=await JobFacts.Load(db,jobs.Select(x=>x.Id).ToList(),ct);
  var failedRuns=await db.Runs.Where(x=>x.TenantId==tenant&&x.Status==RunStatus.Failed).Select(x=>x.JobId).Distinct().ToListAsync(ct);
  var tests=await db.Commands.Where(x=>x.TenantId==tenant&&x.JobId==null&&x.Action==RemoteAction.TestDestination&&x.Status=="Completed").Select(x=>x.DeviceId).Distinct().ToListAsync(ct);
  var restores=await db.Commands.Where(x=>x.TenantId==tenant&&x.JobId!=null&&x.Action==RemoteAction.Restore).Select(x=>new{x.JobId,x.Status,x.CheckedBy,x.Checked,x.Expires}).ToListAsync(ct);
  var pendingTokens=await db.EnrollmentTokens.CountAsync(x=>x.TenantId==tenant&&x.Used==null&&x.Expires>now,ct);
  // A restore through a recovery copy on another device counts for the device the backup came from.
  Guid? Origin(BackupJob job)
  {
   var m=managed.FirstOrDefault(x=>x.DeviceId==job.DeviceId&&x.LocalJobId==job.LocalId);
   if(m is {RestoreOnly:true,SourceManagedJobId:{} source})return managed.FirstOrDefault(x=>x.Id==source)?.DeviceId;
   return job.DeviceId;
  }
  var rows=new List<DeviceProtection>();
  foreach(var d in devices)
  {
   var own=managed.Where(x=>x.DeviceId==d.Id&&!x.RestoreOnly).ToList();
   var backupJobs=jobs.Where(x=>x.DeviceId==d.Id&&!managed.Any(m=>m.RestoreOnly&&m.DeviceId==d.Id&&m.LocalJobId==x.LocalId)).ToList();
   var connected=d.LastHeartbeat is not null&&d.LastHeartbeat>=now.AddMinutes(-o.OfflineMinutes);
   var configuration=own.Count==0?(backupJobs.Count>0?Step.Done:Step.Open)
    :own.Any(x=>x.Status is "Failed" or "Rejected")?Step.Failed
    :own.All(x=>x.AppliedRevision>0&&x.AppliedRevision==x.LatestRevision)?Step.Done:Step.Running;
   var success=backupJobs.Select(x=>facts[x.Id].LastSuccess).Max();
   var running=d.ActiveJobLocalId is not null&&backupJobs.Any(x=>x.LocalId==d.ActiveJobLocalId);
   var firstBackup=success is not null?Step.Done:running?Step.Running:backupJobs.Any(x=>failedRuns.Contains(x.Id))?Step.Failed:Step.Open;
   var mine=restores.Where(r=>jobs.FirstOrDefault(j=>j.Id==r.JobId) is {} job&&Origin(job)==d.Id).ToList();
   var checkedRestore=mine.Where(x=>x.Checked is not null).OrderByDescending(x=>x.Checked).FirstOrDefault();
   var restoreTest=checkedRestore is not null?Step.Done:mine.Any(x=>x.Status=="Completed")?Step.Running
    :mine.Any(x=>x.Status is "Pending" or "AwaitingApproval" or "Accepted"&&x.Expires>now.AddHours(-2))?Step.Running
    :mine.Any(x=>x.Status is "Failed" or "Rejected" or "Indeterminate")?Step.Failed:Step.Open;
   var manualOnly=own.Count>0&&own.All(x=>x.ScheduleRecorded&&x.ScheduleRepeatHours is null);
   rows.Add(new(d,connected,connected&&d.EngineReachable,configuration,success is not null||tests.Contains(d.Id),firstBackup,restoreTest,checkedRestore?.CheckedBy,
    backupJobs.Select(x=>x.NextRun).Where(x=>x is not null).Min(),manualOnly,success));
  }
  var expected=customer.ExpectedDevices??devices.Count;
  return new(expected,devices.Count,pendingTokens,rows.Count(x=>x.Connected),rows.Count(x=>x.Configuration==Step.Done),rows.Count(x=>x.FirstBackup==Step.Done),rows.Count(x=>x.RestoreTest==Step.Done),rows);
 }
 public static (string Text,string Css) Label(Step step,string done,string open,string running="läuft",string failed="fehlgeschlagen")=>step switch
 {Step.Done=>(done,"healthy"),Step.Running=>(running,"waiting"),Step.Failed=>(failed,"critical"),_=>(open,"offline")};
}
