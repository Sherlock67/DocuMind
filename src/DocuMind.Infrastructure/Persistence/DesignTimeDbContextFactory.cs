using DocuMind.Infrastructure.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocuMind.Infrastructure.Persistence;

internal static class SqlServerSetup
{
    public const string DefaultConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=DocuMind;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>
    /// One place that configures the provider, used by the app and by <c>dotnet ef</c> alike.
    /// Compatibility level 170 (SQL Server 2025 / Azure SQL) enables the native VECTOR and JSON types.
    /// </summary>
    public static TBuilder UseDocuMindSqlServer<TBuilder>(this TBuilder options, string connectionString)
        where TBuilder : DbContextOptionsBuilder
    {
        options.UseSqlServer(connectionString, sql => sql
            .UseCompatibilityLevel(170)
            .EnableRetryOnFailure(maxRetryCount: 3));
        return options;
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without booting the web host.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseDocuMindSqlServer(SqlServerSetup.DefaultConnectionString)
            .Options;

        return new AppDbContext(options, Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions()));
    }
}
