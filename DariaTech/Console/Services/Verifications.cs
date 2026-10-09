using DariaTech.Console.Api;
using DariaTech.Console.Data;
using DariaTech.Console.Security;
namespace DariaTech.Console.Services;
// Queues the monthly backup check for every job whose device is online (see CommandApi.QueueDueVerifications).
public sealed class Verifications(IServiceScopeFactory scopes,Microsoft.Extensions.Options.IOptions<MonitoringOptions> monitoring,IConfiguration configuration,ILogger<Verifications> log):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  if(configuration["Verifications:Enabled"]=="false")return;
  using var timer=new PeriodicTimer(TimeSpan.FromMinutes(15));
  do
  {
   try
   {
    using var s=scopes.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Maintenance=true;
    var queued=await CommandApi.QueueDueVerifications(s.ServiceProvider.GetRequiredService<ManagementDb>(),s.ServiceProvider.GetRequiredService<CommandSigning>(),
     s.ServiceProvider.GetRequiredService<ISecretStore>(),DateTimeOffset.UtcNow,monitoring.Value.OfflineMinutes,stoppingToken);
    if(queued>0)log.LogInformation("Queued {Count} monthly backup checks",queued);
   }
   catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
   catch(Exception ex){log.LogError("Backup check scheduling failed ({Type})",ex.GetType().Name);}
  }while(await timer.WaitForNextTickAsync(stoppingToken));
 }
}
