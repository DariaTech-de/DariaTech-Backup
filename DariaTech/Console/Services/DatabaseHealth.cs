using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using DariaTech.Console.Data;
namespace DariaTech.Console.Services;
public sealed class DatabaseHealth(IServiceScopeFactory scopes) : IHealthCheck
{
 public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,CancellationToken ct=default)
 {
  try{using var s=scopes.CreateScope();return await s.ServiceProvider.GetRequiredService<ManagementDb>().Database.CanConnectAsync(ct)?HealthCheckResult.Healthy():HealthCheckResult.Unhealthy("Database unavailable");}
  catch{return HealthCheckResult.Unhealthy("Database unavailable");}
 }
}
