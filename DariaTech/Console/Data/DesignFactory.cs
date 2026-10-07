using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace DariaTech.Console.Data;
public sealed class DesignFactory : IDesignTimeDbContextFactory<ManagementDb>
{
 public ManagementDb CreateDbContext(string[] args)=>new(new DbContextOptionsBuilder<ManagementDb>().UseNpgsql("Host=localhost;Database=dariatech_design").Options,new TenantScope(new HttpContextAccessor()));
}
