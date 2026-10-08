using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync.IntegrationTests.Infrastructure;

/// <summary>A disposable database created for one test on the Aspire-managed SQL Server.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string name)
    {
        Name = name;
        ConnectionString = SqlFixture.ConnectionStringFor(name);
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public static async Task<TestDatabase> CreateAsync(string? collation = null)
    {
        var name = $"SchemaSync_{Guid.NewGuid():N}"[..28];
        var collate = collation is null ? string.Empty : $" COLLATE {collation}";
        await SqlFixture.ExecuteAsync(SqlFixture.ConnectionStringFor("master"), $"CREATE DATABASE [{name}]{collate}");
        return new TestDatabase(name);
    }

    /// <summary>Builds a service provider the way an application would: logging plus AddDbContext.</summary>
    public ServiceProvider BuildServices<TContext>(Action<IServiceCollection>? configure = null, string? connectionString = null)
        where TContext : DbContext
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
        services.AddDbContext<TContext>(options => options.UseSqlServer(connectionString ?? ConnectionString));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public async Task<SchemaSyncResult> ApplyAsync<TContext>(SchemaSyncOptions? options = null, Action<IServiceCollection>? configure = null, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        await using var provider = BuildServices<TContext>(configure);
        return await provider.ApplyDatabaseSchemaAsync<TContext>(options ?? new SchemaSyncOptions(), cancellationToken);
    }

    public Task<SchemaSyncResult> ApplyShapeAsync(SchemaShape shape, SchemaSyncOptions? options = null, CancellationToken cancellationToken = default)
        => ApplyAsync<EvolvingDbContext>(options, services => services.AddSingleton(shape), cancellationToken);

    public EvolvingDbContext OpenShape(SchemaShape shape) => TestContextFactory.Evolving(shape, ConnectionString);

    public MatrixDbContext OpenMatrix() => TestContextFactory.Matrix(ConnectionString);

    public Task ExecuteAsync(params string[] batches) => SqlFixture.ExecuteAsync(ConnectionString, batches);

    public Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters) => SqlFixture.ScalarAsync<T>(ConnectionString, sql, parameters);

    public async Task<int> CountAsync(string qualifiedTable) => await ScalarAsync<int>($"SELECT COUNT(*) FROM {qualifiedTable}");

    public async Task<bool> ObjectExistsAsync(string qualifiedName) => await ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(@name) IS NULL THEN 0 ELSE 1 END", ("@name", qualifiedName)) == 1;

    public async Task<bool> ColumnExistsAsync(string qualifiedTable, string column)
        => await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND name = @column", ("@table", qualifiedTable), ("@column", column)) == 1;

    public async Task<int> TableCountAsync() => await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables");

    public async ValueTask DisposeAsync()
    {
        try
        {
            await SqlFixture.ExecuteAsync(SqlFixture.ConnectionStringFor("master"), $"""
                IF DB_ID(N'{Name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{Name}];
                END
                """);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            // Best effort: the container is discarded after the run anyway.
        }
    }
}
