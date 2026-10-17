using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac.Model;

namespace EFCore.SchemaSync.Deployment;

/// <summary>Picks the DacFx target platform that matches the connected server.</summary>
internal static class TargetPlatform
{
    /// <summary>What the server says about itself.</summary>
    /// <param name="EngineEdition"><c>SERVERPROPERTY('EngineEdition')</c>: 1-4 SQL Server, 5 Azure SQL Database, 6 Synapse dedicated pool, 8 Managed Instance, 9 Azure SQL Edge, 11 Synapse serverless / Fabric warehouse, 12 Fabric SQL database.</param>
    /// <param name="MajorVersion"><c>SERVERPROPERTY('ProductMajorVersion')</c>, for example 16 for SQL Server 2022.</param>
    /// <param name="ProductVersion"><c>SERVERPROPERTY('ProductVersion')</c>, for logs.</param>
    internal sealed record ServerInfo(int EngineEdition, int MajorVersion, string ProductVersion);

    public static async Task<ServerInfo> ReadAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CONVERT(int, SERVERPROPERTY('EngineEdition')),
                   CONVERT(int, SERVERPROPERTY('ProductMajorVersion')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))
            """;
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new ServerInfo(
                reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? "unknown" : reader.GetString(2));
        }
        catch (SqlException ex)
        {
            throw new SchemaSyncException(SchemaSyncStage.Connect, $"Could not read the server edition and version to pick the DacFx target platform: {ex.Message}. Set SchemaSyncOptions.TargetSqlServerVersion explicitly to skip detection.", ex);
        }
    }

    /// <summary>Maps the server to a DacFx platform, or throws for platforms the library does not support.</summary>
    public static SqlServerVersion Resolve(ServerInfo server) => server.EngineEdition switch
    {
        >= 1 and <= 4 => server.MajorVersion switch
        {
            11 => SqlServerVersion.Sql110,
            12 => SqlServerVersion.Sql120,
            13 => SqlServerVersion.Sql130,
            14 => SqlServerVersion.Sql140,
            15 => SqlServerVersion.Sql150,
            16 => SqlServerVersion.Sql160,
            >= 17 => SqlServerVersion.Sql170,
            _ => throw Unsupported(server, $"SQL Server major version {server.MajorVersion}"),
        },
        5 => SqlServerVersion.SqlAzure,
        8 => SqlServerVersion.Sql160,
        9 => SqlServerVersion.Sql150,
        6 => throw Unsupported(server, "Azure Synapse Analytics dedicated SQL pool"),
        11 => throw Unsupported(server, "Azure Synapse serverless SQL pool or Microsoft Fabric warehouse"),
        12 => throw Unsupported(server, "SQL database in Microsoft Fabric"),
        _ => throw Unsupported(server, $"engine edition {server.EngineEdition}"),
    };

    public static string Describe(ServerInfo server) => server.EngineEdition switch
    {
        5 => $"Azure SQL Database {server.ProductVersion}",
        8 => $"Azure SQL Managed Instance {server.ProductVersion}",
        9 => $"Azure SQL Edge {server.ProductVersion}",
        _ => $"SQL Server {server.ProductVersion}",
    };

    private static SchemaSyncException Unsupported(ServerInfo server, string what)
        => new(SchemaSyncStage.Connect, $"EFCore.SchemaSync does not support {what} (EngineEdition {server.EngineEdition}, version {server.ProductVersion}). If you know the platform is compatible, set SchemaSyncOptions.TargetSqlServerVersion explicitly to skip detection.");
}
