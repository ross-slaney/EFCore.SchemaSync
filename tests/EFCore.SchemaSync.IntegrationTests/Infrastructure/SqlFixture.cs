using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Data.SqlClient;

namespace EFCore.SchemaSync.IntegrationTests.Infrastructure;

/// <summary>
/// Starts the Aspire test AppHost once per test run: one throwaway SQL Server 2022 container plus the sample API.
/// Tests never touch an application connection string; every test creates its own disposable database on this server.
/// </summary>
[TestClass]
public sealed class SqlFixture
{
    private static DistributedApplication? _app;

    public static DistributedApplication App => _app ?? throw new InvalidOperationException("The Aspire test host is not running.");

    /// <summary>Server-level connection string (no database) for the SQL Server container.</summary>
    public static string ServerConnectionString { get; private set; } = string.Empty;

    [AssemblyInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.EFCore_SchemaSync_IntegrationTests_AppHost>();
        _app = await builder.BuildAsync();
        await _app.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("sql", cts.Token);

        var raw = await _app.GetConnectionStringAsync("sql", cts.Token)
            ?? throw new InvalidOperationException("Aspire did not provide a connection string for the 'sql' resource.");
        ServerConnectionString = WithLocalDefaults(raw);
        context.WriteLine($"SQL Server container is healthy at {new SqlConnectionStringBuilder(ServerConnectionString).DataSource}");
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    public static string ConnectionStringFor(string databaseName)
        => WithLocalDefaults(new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = databaseName }.ConnectionString);

    public static string SaPassword => new SqlConnectionStringBuilder(ServerConnectionString).Password;

    public static async Task ExecuteAsync(string connectionString, params string[] batches)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in batches)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null || result is DBNull ? default : (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string WithLocalDefaults(string connectionString)
        => new SqlConnectionStringBuilder(connectionString) { TrustServerCertificate = true, Encrypt = false }.ConnectionString;
}
