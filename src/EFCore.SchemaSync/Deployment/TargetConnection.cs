using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.SqlServer.Dac;

namespace EFCore.SchemaSync.Deployment;

/// <summary>The resolved connection to the target database. Never logged in full: it may contain credentials.</summary>
internal sealed class TargetConnection
{
    private TargetConnection(string connectionString, string? accessToken, string serverName, string databaseName)
    {
        ConnectionString = connectionString;
        AccessToken = accessToken;
        ServerName = serverName;
        DatabaseName = databaseName;
    }

    public string ConnectionString { get; }

    /// <summary>Azure AD / Entra access token carried by the context's SqlConnection, if any.</summary>
    public string? AccessToken { get; }

    public string ServerName { get; }

    public string DatabaseName { get; }

    public IUniversalAuthProvider? AuthProvider => AccessToken is null ? null : new StaticTokenAuthProvider(AccessToken);

    public static TargetConnection Resolve(DbContext context, SchemaSyncOptions options)
    {
        if (!context.Database.IsSqlServer())
        {
            throw new SchemaSyncException(
                SchemaSyncStage.ResolveContext,
                $"{context.GetType().Name} is configured for provider '{context.Database.ProviderName ?? "(none)"}'. EFCore.SchemaSync supports Microsoft.EntityFrameworkCore.SqlServer only.");
        }

        string? connectionString = options.DeploymentConnectionString;
        string? accessToken = null;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                var dbConnection = context.Database.GetDbConnection();
                connectionString = context.Database.GetConnectionString();
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    connectionString = dbConnection.ConnectionString;
                }

                if (dbConnection is SqlConnection { AccessToken: { Length: > 0 } token })
                {
                    accessToken = token;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                throw new SchemaSyncException(SchemaSyncStage.ResolveContext, $"Could not read the connection configured on {context.GetType().Name}: {ex.Message}", ex);
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new SchemaSyncException(
                SchemaSyncStage.ResolveContext,
                $"{context.GetType().Name} has no connection string. Configure UseSqlServer with a connection string or set SchemaSyncOptions.DeploymentConnectionString.");
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new SchemaSyncException(SchemaSyncStage.ResolveContext, "The connection string could not be parsed: " + ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new SchemaSyncException(
                SchemaSyncStage.ResolveContext,
                "The connection string does not name a database (Initial Catalog / Database). EFCore.SchemaSync deploys to exactly one existing database.");
        }

        return new TargetConnection(connectionString, accessToken, builder.DataSource, builder.InitialCatalog);
    }

    /// <summary>
    /// Opens the connection that owns the schema lock. Pooling is disabled so the session (and with it the
    /// session-owned application lock) ends when the connection is disposed instead of lingering in the pool.
    /// </summary>
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString);
        if (AccessToken is not null)
        {
            connection.AccessToken = AccessToken;
        }

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            var hint = ex is SqlException { Number: 4060 }
                ? $" Database '{DatabaseName}' does not exist or is not accessible. EFCore.SchemaSync does not create databases."
                : string.Empty;
            throw new SchemaSyncException(SchemaSyncStage.Connect, $"Could not open a connection to {ServerName}/{DatabaseName}: {ex.Message}{hint}", ex);
        }
    }

    private sealed class StaticTokenAuthProvider(string token) : IUniversalAuthProvider
    {
        public string GetValidAccessToken() => token;
    }
}
