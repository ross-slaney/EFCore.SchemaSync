using System.Text.Json;
using EFCore.SchemaSync.Conversion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// The library's bookkeeping table, <c>dbo.__SchemaSyncState</c>: one row per DbContext with the model hash and schema
/// fingerprint of its last successful application. The table is part of every deployed model (so DacFx never reports
/// or drops it) and is hidden from results. Reading or writing it never fails an application: problems only disable
/// the fast path for that start.
/// </summary>
internal static class SchemaState
{
    public const string QualifiedTableName = "[dbo].[__SchemaSyncState]";
    public const string PrimaryKeyName = "[dbo].[PK___SchemaSyncState]";

    private const string CreateTableSql = """
        CREATE TABLE [dbo].[__SchemaSyncState] (
            [ContextKey] nvarchar(256) NOT NULL,
            [ModelHash] char(64) NOT NULL,
            [SchemaFingerprint] char(64) NOT NULL,
            [LibraryVersion] nvarchar(128) NOT NULL,
            [AppliedAt] datetime2 NOT NULL,
            [RetainedObjects] nvarchar(max) NULL,
            [Warnings] nvarchar(max) NULL,
            CONSTRAINT [PK___SchemaSyncState] PRIMARY KEY ([ContextKey])
        );
        """;

    public static NormalizedStatement Statement { get; } = new("000_Table_dbo.__SchemaSyncState.sql", "Table", CreateTableSql);

    /// <summary>The stored state of one context.</summary>
    public sealed record Entry(string ModelHash, string SchemaFingerprint, IReadOnlyList<SchemaObjectReference> RetainedObjects, IReadOnlyList<string> Warnings);

    /// <summary>True for the state table and its primary key (as named in deploy reports and object lists).</summary>
    public static bool IsInternal(string objectName)
        => objectName.StartsWith(QualifiedTableName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(objectName, PrimaryKeyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the state table exists in the target (it then stays in the model even with the fast path off).</summary>
    public static async Task<bool> ExistsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.__SchemaSyncState', N'U') IS NULL THEN 0 ELSE 1 END", connection) { CommandTimeout = 30 };
            return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0) == 1;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    public static async Task<Entry?> TryReadAsync(SqlConnection connection, string contextKey, ILogger logger, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.__SchemaSyncState', N'U') IS NOT NULL
                SELECT [ModelHash], [SchemaFingerprint], [RetainedObjects], [Warnings]
                FROM [dbo].[__SchemaSyncState] WHERE [ContextKey] = @key;
            """;
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
            command.Parameters.Add(new SqlParameter("@key", System.Data.SqlDbType.NVarChar, 256) { Value = contextKey });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var retained = reader.IsDBNull(2) ? [] : JsonSerializer.Deserialize<List<StoredObject>>(reader.GetString(2))?.Select(o => new SchemaObjectReference(o.Type, o.Name)).ToList() ?? [];
            var warnings = reader.IsDBNull(3) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [];
            return new Entry(reader.GetString(0), reader.GetString(1), retained, warnings);
        }
        catch (Exception ex) when (ex is SqlException or JsonException or InvalidCastException)
        {
            logger.LogDebug(ex, "EFCore.SchemaSync: could not read the schema state; running the full comparison.");
            return null;
        }
    }

    public static async Task TryWriteAsync(SqlConnection connection, string contextKey, Entry entry, ILogger logger, CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE [dbo].[__SchemaSyncState] WITH (HOLDLOCK) AS t
            USING (SELECT @key AS [ContextKey]) AS s ON t.[ContextKey] = s.[ContextKey]
            WHEN MATCHED THEN UPDATE SET [ModelHash] = @model, [SchemaFingerprint] = @fingerprint, [LibraryVersion] = @version,
                [AppliedAt] = SYSUTCDATETIME(), [RetainedObjects] = @retained, [Warnings] = @warnings
            WHEN NOT MATCHED THEN INSERT ([ContextKey], [ModelHash], [SchemaFingerprint], [LibraryVersion], [AppliedAt], [RetainedObjects], [Warnings])
                VALUES (@key, @model, @fingerprint, @version, SYSUTCDATETIME(), @retained, @warnings);
            """;
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
            command.Parameters.Add(new SqlParameter("@key", System.Data.SqlDbType.NVarChar, 256) { Value = contextKey });
            command.Parameters.Add(new SqlParameter("@model", System.Data.SqlDbType.Char, 64) { Value = entry.ModelHash });
            command.Parameters.Add(new SqlParameter("@fingerprint", System.Data.SqlDbType.Char, 64) { Value = entry.SchemaFingerprint });
            command.Parameters.Add(new SqlParameter("@version", System.Data.SqlDbType.NVarChar, 128) { Value = ModelHash.LibraryVersion });
            command.Parameters.Add(new SqlParameter("@retained", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(entry.RetainedObjects.Select(o => new StoredObject(o.ObjectType, o.Name))) });
            command.Parameters.Add(new SqlParameter("@warnings", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(entry.Warnings) });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            logger.LogWarning("EFCore.SchemaSync: could not record the schema state ({Message}); the next start runs the full comparison. Grant the deploying principal INSERT/UPDATE on dbo.__SchemaSyncState to enable the fast path.", ex.Message);
        }
    }

    private sealed record StoredObject(string Type, string Name);
}
