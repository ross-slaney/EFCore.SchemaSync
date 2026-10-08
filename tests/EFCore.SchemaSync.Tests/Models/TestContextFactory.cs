using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.Tests.Models;

/// <summary>Creates contexts configured for SQL Server without opening a connection (conversion never needs one).</summary>
public static class TestContextFactory
{
    public const string OfflineConnectionString = "Server=(local);Database=EFCoreSchemaSyncUnitTests;Integrated Security=true;TrustServerCertificate=true";

    public static DbContextOptions<TContext> SqlServerOptions<TContext>(string? connectionString = null)
        where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(connectionString ?? OfflineConnectionString)
            .Options;

    public static MatrixDbContext Matrix(string? connectionString = null) => new(SqlServerOptions<MatrixDbContext>(connectionString));

    public static EvolvingDbContext Evolving(SchemaShape shape, string? connectionString = null) => new(SqlServerOptions<EvolvingDbContext>(connectionString), shape);
}
