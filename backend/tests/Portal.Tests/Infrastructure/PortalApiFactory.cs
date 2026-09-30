using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace Portal.Tests.Infrastructure;

/// <summary>
/// Boots the real API against a real SQL Server database that exists only for this factory. The server
/// comes from <c>PORTAL_TEST_CONNECTION</c> (no database name; one is generated). The database is created
/// by applying the EF migrations and dropped when the factory is disposed.
/// </summary>
public sealed class PortalApiFactory : WebApplicationFactory<Program>
{
    private const string ConnectionVariable = "PORTAL_TEST_CONNECTION";
    private const string DefaultServer = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    private readonly string _databaseName = $"PortalTest_{Guid.NewGuid():N}";
    private readonly string _serverConnection =
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } configured ? configured : DefaultServer;

    private string DatabaseConnection => new SqlConnectionStringBuilder(_serverConnection)
    {
        InitialCatalog = _databaseName,
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", DatabaseConnection);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimit:AuthPerMinute", "100000");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await DropDatabaseAsync();
        GC.SuppressFinalize(this);
    }

    private async Task DropDatabaseAsync()
    {
        await using var connection = new SqlConnection(_serverConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID(N'{_databaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_databaseName}]; END";
        await command.ExecuteNonQueryAsync();
    }
}
