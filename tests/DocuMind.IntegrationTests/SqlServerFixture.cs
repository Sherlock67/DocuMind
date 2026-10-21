using DocuMind.Infrastructure.AI;
using DocuMind.Infrastructure.Persistence;
using DocuMind.Infrastructure.Search;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuMind.IntegrationTests;

/// <summary>
/// Creates a throw-away database on SQL Server 2025 (LocalDB by default, or DOCUMIND_TEST_SQLSERVER),
/// applies the real migrations, and drops it afterwards. Tests skip when no server is reachable.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string DefaultServer = "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _server = Environment.GetEnvironmentVariable("DOCUMIND_TEST_SQLSERVER") is { Length: > 0 } s ? s : DefaultServer;

    public string? SkipReason { get; private set; }
    public string ConnectionString { get; private set; } = "";
    public SqlConnections Connections => new(ConnectionString, ConnectionString);
    public IOptions<EmbeddingOptions> Embedding { get; } = Options.Create(new EmbeddingOptions());

    public async Task InitializeAsync()
    {
        var database = $"DocuMind_Tests_{Guid.NewGuid():N}";
        ConnectionString = new SqlConnectionStringBuilder(_server) { InitialCatalog = database }.ConnectionString;

        try
        {
            await using var probe = new SqlConnection(_server);
            await probe.OpenAsync();
            await using var version = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)", probe);
            if ((int)(await version.ExecuteScalarAsync())! < 17)
            {
                SkipReason = "SQL Server 2025 (17.x) or later is required for the VECTOR type.";
                return;
            }
        }
        catch (SqlException ex)
        {
            SkipReason = $"No SQL Server reachable ({ex.Message.Split('\n')[0]}). Set DOCUMIND_TEST_SQLSERVER to run these tests.";
            return;
        }

        await using var db = CreateWriteContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (SkipReason is not null) return;
        await using var db = CreateWriteContext();
        await db.Database.EnsureDeletedAsync();
    }

    public AppDbContext CreateWriteContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseDocuMindSqlServer(ConnectionString).Options, Embedding);

    public ReadDbContext CreateReadContext() =>
        new(new DbContextOptionsBuilder<ReadDbContext>()
            .UseDocuMindSqlServer(ConnectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options, Embedding);

    public void SkipIfUnavailable() => Skip.If(SkipReason is not null, SkipReason);
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
