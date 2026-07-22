using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harness.Audit;

/// <summary>
/// Design-time only: lets `dotnet ef migrations add` build the model without booting the API or
/// reaching a database. The connection string comes from <c>HARNESS_DB_CONNECTION</c> and the
/// fallback is a local placeholder that carries no password — real credentials live in the
/// environment (see docker/compose.yaml), never in source.
/// </summary>
public sealed class HarnessDbContextFactory : IDesignTimeDbContextFactory<HarnessDbContext>
{
    public const string ConnectionEnvVar = "HARNESS_DB_CONNECTION";
    public const string LocalDefaultConnection = "Host=localhost;Port=5432;Database=harness;Username=harness";

    public HarnessDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(connection)) connection = LocalDefaultConnection;

        var options = new DbContextOptionsBuilder<HarnessDbContext>()
            .UseNpgsql(connection)
            .Options;
        return new HarnessDbContext(options);
    }
}
