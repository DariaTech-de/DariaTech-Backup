using DariaTech.Console.Data;
using DariaTech.Console.Services;
using DariaTech.Contracts;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace DariaTech.Console.Pages;
public sealed record DayRuns(DateOnly Day,int Success,int Warning,int Failed){public int Total=>Success+Warning+Failed;}
public sealed record AlertRow(Alert Alert,string Device,Guid DeviceId);
public sealed class IndexModel(ManagementDb db,IOptions<MonitoringOptions> options):PageModel
{
 public List<DeviceRow> Rows {get;private set;}=[];public int Customers{get;private set;}public int Successes{get;private set;}
 public int Sites{get;private set;}public List<DayRuns> Days{get;private set;}=[];public List<AlertRow> OpenAlerts{get;private set;}=[];public int OpenAlertCount{get;private set;}
 public IEnumerable<DeviceRow> Running=>Rows.Where(x=>x.Device.Progress is not null);
 public int Count(string status)=>Rows.Count(x=>x.Status==status);
 public async Task OnGetAsync()
 {
  Rows=await Overview.Load(db,options.Value);Customers=await db.Customers.CountAsync();Sites=await db.Sites.CountAsync();var since=DateTimeOffset.UtcNow.AddHours(-24);Successes=await db.Runs.CountAsync(x=>x.Status==RunStatus.Success&&x.Completed>=since);
  var today=DateOnly.FromDateTime(DateTime.UtcNow);var from=new DateTimeOffset(today.AddDays(-13).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
  var runs=await db.Runs.Where(x=>x.Started>=from).Select(x=>new{x.Started,x.Status}).ToListAsync();
  Days=Enumerable.Range(0,14).Select(i=>today.AddDays(i-13)).Select(d=>{var r=runs.Where(x=>DateOnly.FromDateTime(x.Started.UtcDateTime)==d).ToList();return new DayRuns(d,r.Count(x=>x.Status==RunStatus.Success),r.Count(x=>x.Status is RunStatus.Warning or RunStatus.Cancelled or RunStatus.Unknown),r.Count(x=>x.Status==RunStatus.Failed));}).ToList();
  var open=db.Alerts.Where(x=>x.Resolved==null);OpenAlertCount=await open.CountAsync();
  var alerts=await open.OrderBy(x=>x.Severity=="Critical"?0:1).ThenByDescending(x=>x.Opened).Take(6).ToListAsync();
  OpenAlerts=alerts.Select(a=>new AlertRow(a,Rows.FirstOrDefault(r=>r.Device.Id==a.DeviceId)?.Device.Name??"Unbekanntes Gerät",a.DeviceId)).ToList();
 }
}
