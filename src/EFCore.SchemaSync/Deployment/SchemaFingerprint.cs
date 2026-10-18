using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// A SHA-256 over the catalog views that describe everything DacFx compares: objects (with their modify dates),
/// columns, computed columns, defaults, checks, indexes and their columns, foreign keys, identity, sequences, module
/// text, extended properties, temporal settings, user types and synonyms. Any DDL against the database changes it;
/// data changes do not.
/// </summary>
internal static class SchemaFingerprint
{
    internal const string Sql = """
        SELECT s.name FROM sys.schemas s WHERE s.schema_id = 1 OR s.schema_id BETWEEN 5 AND 16383 ORDER BY s.name;
        SELECT SCHEMA_NAME(o.schema_id), o.name, o.type, OBJECT_NAME(o.parent_object_id), o.create_date, o.modify_date
            FROM sys.objects o WHERE o.is_ms_shipped = 0 ORDER BY 1, 2, 3;
        SELECT SCHEMA_NAME(o.schema_id), o.name, c.column_id, c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale,
               c.is_nullable, c.collation_name, c.is_identity, c.is_computed, c.is_sparse, c.generated_always_type, c.is_hidden
            FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id WHERE o.is_ms_shipped = 0 ORDER BY 1, 2, 3;
        SELECT OBJECT_SCHEMA_NAME(cc.object_id), OBJECT_NAME(cc.object_id), cc.name, cc.definition, cc.is_persisted
            FROM sys.computed_columns cc ORDER BY 1, 2, 3;
        SELECT OBJECT_SCHEMA_NAME(d.parent_object_id), OBJECT_NAME(d.parent_object_id), d.name, d.parent_column_id, d.definition
            FROM sys.default_constraints d ORDER BY 1, 2, 3;
        SELECT OBJECT_SCHEMA_NAME(k.parent_object_id), OBJECT_NAME(k.parent_object_id), k.name, k.definition, k.is_disabled
            FROM sys.check_constraints k ORDER BY 1, 2, 3;
        SELECT OBJECT_SCHEMA_NAME(i.object_id), OBJECT_NAME(i.object_id), i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint,
               i.filter_definition, i.is_disabled, ic.key_ordinal, ic.index_column_id, COL_NAME(ic.object_id, ic.column_id), ic.is_descending_key, ic.is_included_column
            FROM sys.indexes i JOIN sys.objects o ON o.object_id = i.object_id
            LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            WHERE o.is_ms_shipped = 0 AND i.type > 0 ORDER BY 1, 2, 3, 11;
        SELECT OBJECT_SCHEMA_NAME(f.parent_object_id), OBJECT_NAME(f.parent_object_id), f.name, OBJECT_SCHEMA_NAME(f.referenced_object_id), OBJECT_NAME(f.referenced_object_id),
               f.delete_referential_action, f.update_referential_action, f.is_disabled, fc.constraint_column_id,
               COL_NAME(fc.parent_object_id, fc.parent_column_id), COL_NAME(fc.referenced_object_id, fc.referenced_column_id)
            FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = f.object_id ORDER BY 1, 2, 3, 9;
        SELECT OBJECT_SCHEMA_NAME(ic.object_id), OBJECT_NAME(ic.object_id), ic.name, CONVERT(nvarchar(64), ic.seed_value), CONVERT(nvarchar(64), ic.increment_value)
            FROM sys.identity_columns ic JOIN sys.objects o ON o.object_id = ic.object_id WHERE o.is_ms_shipped = 0 ORDER BY 1, 2, 3;
        SELECT SCHEMA_NAME(s.schema_id), s.name, TYPE_NAME(s.user_type_id), CONVERT(nvarchar(64), s.start_value), CONVERT(nvarchar(64), s.increment),
               CONVERT(nvarchar(64), s.minimum_value), CONVERT(nvarchar(64), s.maximum_value), s.is_cycling, s.is_cached, s.cache_size
            FROM sys.sequences s ORDER BY 1, 2;
        SELECT OBJECT_SCHEMA_NAME(m.object_id), OBJECT_NAME(m.object_id), HASHBYTES('SHA2_256', m.definition)
            FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.is_ms_shipped = 0 ORDER BY 1, 2;
        SELECT ep.class, ep.major_id, ep.minor_id, ep.name, HASHBYTES('SHA2_256', CONVERT(nvarchar(max), ep.value))
            FROM sys.extended_properties ep ORDER BY 1, 2, 3, 4;
        SELECT SCHEMA_NAME(t.schema_id), t.name, t.temporal_type, OBJECT_NAME(t.history_table_id), t.is_memory_optimized
            FROM sys.tables t WHERE t.is_ms_shipped = 0 ORDER BY 1, 2;
        SELECT SCHEMA_NAME(t.schema_id), t.name, t.max_length, t.precision, t.scale, t.is_nullable, t.is_table_type
            FROM sys.types t WHERE t.is_user_defined = 1 ORDER BY 1, 2;
        SELECT SCHEMA_NAME(s.schema_id), s.name, s.base_object_name FROM sys.synonyms s ORDER BY 1, 2;
        """;

    /// <summary>Computes the fingerprint, or returns null (disabling the fast path) when the catalog cannot be read.</summary>
    public static async Task<string?> TryComputeAsync(SqlConnection connection, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var command = new SqlCommand(Sql, connection) { CommandTimeout = 120 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var resultSet = 0;
            do
            {
                Append(hash, $"#set{resultSet++}");
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    Append(hash, "#row");
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        Append(hash, Format(reader.GetValue(i)));
                    }
                }
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (SqlException ex)
        {
            logger.LogDebug(ex, "EFCore.SchemaSync: could not fingerprint the schema; running the full comparison.");
            return null;
        }
    }

    internal static string Format(object value) => value switch
    {
        DBNull => "␀",
        DateTime date => date.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(BitConverter.GetBytes(value.Length));
        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }
}
