using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SqlServer.Dac.Model;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>
/// Opt-in tests against a real Azure SQL Database. They run only when configured and report Inconclusive otherwise:
/// <list type="bullet">
/// <item><c>SCHEMASYNC_AZURE_SQL_CONNECTION_STRING</c>: a SQL-authenticated connection string to a disposable database
/// the tests may create and drop schemas in.</item>
/// <item><c>SCHEMASYNC_AZURE_SQL_ACCESS_TOKEN</c> (optional): an Entra ID access token for the same server
/// (for example <c>az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv</c>);
/// combined with the connection string minus its credentials.</item>
/// </list>
/// Each test works in its own schema and removes it afterwards.
/// </summary>
[TestClass]
[TestCategory("AzureSql")]
public sealed class AzureSqlTests
{
    private static string ConnectionString()
        => Environment.GetEnvironmentVariable("SCHEMASYNC_AZURE_SQL_CONNECTION_STRING") is { Length: > 0 } value
            ? value
            : throw new AssertInconclusiveException("Set SCHEMASYNC_AZURE_SQL_CONNECTION_STRING to run the Azure SQL Database tests.");

    [TestMethod]
    public async Task Azure_SQL_Database_is_detected_and_deployed_to()
    {
        var connectionString = ConnectionString();
        var schema = $"ss_{Guid.NewGuid():N}"[..20];
        try
        {
            var first = await ApplyAsync(schema, options => options.UseSqlServer(connectionString));
            var second = await ApplyAsync(schema, options => options.UseSqlServer(connectionString));

            Assert.AreEqual(SqlServerVersion.SqlAzure, first.TargetPlatform);
            Assert.AreEqual(SchemaSyncOutcome.Applied, first.Outcome, first.Describe());
            Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, second.Outcome, second.Describe());
        }
        finally
        {
            await DropSchemaAsync(connectionString, schema);
        }
    }

    [TestMethod]
    public async Task An_Entra_access_token_on_the_context_connection_is_used()
    {
        var connectionString = ConnectionString();
        var token = Environment.GetEnvironmentVariable("SCHEMASYNC_AZURE_SQL_ACCESS_TOKEN") is { Length: > 0 } value
            ? value
            : throw new AssertInconclusiveException("Set SCHEMASYNC_AZURE_SQL_ACCESS_TOKEN to run the Entra ID test.");
        var withoutCredentials = new SqlConnectionStringBuilder(connectionString);
        withoutCredentials.Remove("User ID");
        withoutCredentials.Remove("Password");
        withoutCredentials.Remove("Authentication");
        var schema = $"ss_{Guid.NewGuid():N}"[..20];
        try
        {
            var result = await ApplyAsync(schema, options => options.UseSqlServer(new SqlConnection(withoutCredentials.ConnectionString) { AccessToken = token }));

            Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        }
        finally
        {
            await DropSchemaAsync(connectionString, schema);
        }
    }

    private static async Task<SchemaSyncResult> ApplyAsync(string schema, Action<DbContextOptionsBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AzureSchema(schema));
        services.AddDbContext<AzureContext>(configure);
        await using var provider = services.BuildServiceProvider();
        // The shared test database may hold other runs' schemas: don't report them as retained objects.
        return await provider.ApplyDatabaseSchemaAsync<AzureContext>(o => o.ReportRetainedObjects = false);
    }

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF OBJECT_ID(N'[{schema}].[Notes]') IS NOT NULL DROP TABLE [{schema}].[Notes];
            IF SCHEMA_ID(N'{schema}') IS NOT NULL EXEC(N'DROP SCHEMA [{schema}]');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public sealed record AzureSchema(string Name);

    public sealed class Note
    {
        public int Id { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    public sealed class AzureContext(DbContextOptions<AzureContext> options, AzureSchema schema) : DbContext(options)
    {
        public DbSet<Note> Notes => Set<Note>();

        public string SchemaName => schema.Name;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.ReplaceService<IModelCacheKeyFactory, SchemaCacheKey>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema(schema.Name);
            modelBuilder.Entity<Note>(e => e.Property(p => p.Text).HasMaxLength(200));
        }

        private sealed class SchemaCacheKey : IModelCacheKeyFactory
        {
            public object Create(DbContext context, bool designTime) => (context.GetType(), ((AzureContext)context).SchemaName, designTime);
        }
    }
}
