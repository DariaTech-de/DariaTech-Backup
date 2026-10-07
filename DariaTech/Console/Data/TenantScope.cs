using System.Security.Claims;
namespace DariaTech.Console.Data;

public sealed class TenantScope(IHttpContextAccessor accessor)
{
 // Set only by authenticated agent middleware or an explicit local maintenance command.
 internal bool Maintenance { get; set; }
 internal Guid? AgentTenant { get; set; }
 public bool Global => AgentTenant is null && (Maintenance || accessor.HttpContext?.User.IsInRole("SuperAdmin") == true
    || accessor.HttpContext?.User.IsInRole("Administrator") == true
    || accessor.HttpContext?.User.IsInRole("Technician") == true
    || accessor.HttpContext?.User.IsInRole("ReadOnly") == true);
 public Guid? TenantId => AgentTenant ?? (Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("tenant"), out var id) ? id : null);
 public bool Allows(Guid id) => Global || TenantId == id;
}
