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
    /// <summary>Small on purpose so the size-limit tests do not have to build a huge file.</summary>
    public const long UploadLimitBytes = 64 * 1024;

    public const string SiteDomain = "sites.test";

    private const string ConnectionVariable = "PORTAL_TEST_CONNECTION";
    private const string DefaultServer = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    private readonly string _databaseName = $"PortalTest_{Guid.NewGuid():N}";
    private readonly string _serverConnection =
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } configured ? configured : DefaultServer;

    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"portal-tests-{Guid.NewGuid():N}");

    public string StorageRoot => _storageRoot;

    private string DatabaseConnection => new SqlConnectionStringBuilder(_serverConnection)
    {
        InitialCatalog = _databaseName,
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", DatabaseConnection);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Storage:Root", _storageRoot);

        // "Deploy on Our Platform": a real worker, real git and node builds, and sites served by the API itself.
        // Only the sandbox is the unisolated local one, and health checks go through this test server.
        builder.UseSetting("Deploy:Root", Path.Combine(_storageRoot, "deploy"));
        builder.UseSetting("Deploy:BaseDomain", SiteDomain);
        builder.UseSetting("Deploy:PublicScheme", "http");
        builder.UseSetting("Deploy:Sandbox", "local-unsafe");
        builder.UseSetting("Deploy:AllowLocalRepositories", "true");
        builder.UseSetting("Deploy:HealthCheckAttempts", "2");
        builder.UseSetting("Deploy:BuildTimeoutSeconds", "120");
        builder.UseSetting("DataProtection:KeysPath", Path.Combine(_storageRoot, "keys"));
        builder.ConfigureServices(services =>
            services.AddHttpClient(Portal.Infrastructure.Deployments.StaticSiteDeploymentProvider.HealthClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler()));
        builder.UseSetting("Storage:MaxUploadBytes", UploadLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RateLimit:AuthPerMinute", "100000");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");

        // Racing idempotency claims deliberately hit the unique index; EF logs that as an error each time.
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore.Update", "None");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "None");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await DropDatabaseAsync();
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }

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
