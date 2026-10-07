using DariaTech.Console.Data;
using Microsoft.EntityFrameworkCore;
namespace DariaTech.Console.Security;

public static class DeviceAuthentication
{
 public static async Task<Agent?> Authenticate(HttpContext ctx,ManagementDb db,TenantScope scope)
 {
  var auth=ctx.Request.Headers.Authorization.ToString();
  if(!auth.StartsWith("Bearer ",StringComparison.Ordinal)||auth.Length!=71||!Guid.TryParse(ctx.Request.Headers["X-Device-Id"],out var id))return null;
  var hash=Tokens.Hash(auth[7..]);
  var agent=await db.Agents.IgnoreQueryFilters().SingleOrDefaultAsync(x=>x.DeviceId==id&&x.CredentialHash==hash&&!x.Revoked);
  if(agent is null)return null;
  scope.AgentTenant=agent.TenantId;
  return await db.Customers.AnyAsync(x=>x.TenantId==agent.TenantId&&x.Active)&&await db.Devices.AnyAsync(x=>x.Id==id&&x.Active)?agent:null;
 }
}
