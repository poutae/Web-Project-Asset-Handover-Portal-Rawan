using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Portal.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> at design time.</summary>
public sealed class PortalDbContextFactory : IDesignTimeDbContextFactory<PortalDbContext>
{
    public PortalDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Server=localhost;Database=PortalDev;Integrated Security=true;TrustServerCertificate=true";

        var options = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new PortalDbContext(options, new NullTenantContext());
    }
}
