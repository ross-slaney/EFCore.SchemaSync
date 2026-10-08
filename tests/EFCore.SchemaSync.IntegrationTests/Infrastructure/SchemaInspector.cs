using Microsoft.Data.SqlClient;

namespace EFCore.SchemaSync.IntegrationTests.Infrastructure;

public sealed record ColumnInfo(
    string DataType,
    int Length,
    byte Precision,
    byte Scale,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    bool IsPersisted,
    string? Definition,
    string? DefaultDefinition,
    string? Collation,
    bool IsSparse);

public sealed record IndexInfo(bool IsUnique, bool IsClustered, bool IsPrimaryKey, string? Filter, string KeyColumns, string IncludedColumns, int FillFactor);

public sealed record ForeignKeyInfo(string ReferencedTable, string DeleteAction, string Columns);

/// <summary>Reads the live catalog so tests assert on what SQL Server actually has, not on what DacFx reported.</summary>
public static class SchemaInspector
{
    public static async Task<ColumnInfo?> ColumnAsync(TestDatabase db, string qualifiedTable, string column)
    {
        const string sql = """
            SELECT t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
                   ISNULL(cc.is_persisted, 0), cc.definition, dc.definition, c.collation_name, c.is_sparse
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column
            """;
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", qualifiedTable);
        command.Parameters.AddWithValue("@column", column);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var dataType = reader.GetString(0);
        int maxLength = reader.GetInt16(1);
        var length = maxLength > 0 && dataType is "nvarchar" or "nchar" ? maxLength / 2 : maxLength;
        return new ColumnInfo(
            dataType,
            length,
            reader.GetByte(2),
            reader.GetByte(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetBoolean(11));
    }

    public static async Task<IndexInfo?> IndexAsync(TestDatabase db, string qualifiedTable, string indexName)
    {
        const string sql = """
            SELECT i.is_unique, CASE WHEN i.type = 1 THEN 1 ELSE 0 END, i.is_primary_key, i.filter_definition, i.fill_factor,
                   ISNULL(STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END END, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal, ic.index_column_id), ''),
                   ISNULL(STRING_AGG(CASE WHEN ic.is_included_column = 1 THEN c.name END, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal, ic.index_column_id), '')
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND i.name = @index
            GROUP BY i.is_unique, i.type, i.is_primary_key, i.filter_definition, i.fill_factor
            """;
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", qualifiedTable);
        command.Parameters.AddWithValue("@index", indexName);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new IndexInfo(reader.GetBoolean(0), reader.GetInt32(1) == 1, reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(5), reader.GetString(6), reader.GetByte(4));
    }

    public static async Task<ForeignKeyInfo?> ForeignKeyAsync(TestDatabase db, string constraintName)
    {
        const string sql = """
            SELECT SCHEMA_NAME(rt.schema_id) + '.' + rt.name, fk.delete_referential_action_desc,
                   STRING_AGG(pc.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
            FROM sys.foreign_keys fk
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            WHERE fk.name = @name
            GROUP BY rt.schema_id, rt.name, fk.delete_referential_action_desc
            """;
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@name", constraintName);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new ForeignKeyInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public static async Task<List<string>> TablesAsync(TestDatabase db)
    {
        const string sql = "SELECT SCHEMA_NAME(schema_id) + '.' + name FROM sys.tables ORDER BY 1";
        await using var connection = new SqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public static Task<string?> IdentityAsync(TestDatabase db, string qualifiedTable)
        => db.ScalarAsync<string>("SELECT CONVERT(nvarchar(50), seed_value) + '/' + CONVERT(nvarchar(50), increment_value) FROM sys.identity_columns WHERE object_id = OBJECT_ID(@table)", ("@table", qualifiedTable));

    public static Task<string?> CheckConstraintAsync(TestDatabase db, string constraintName)
        => db.ScalarAsync<string>("SELECT definition FROM sys.check_constraints WHERE name = @name", ("@name", constraintName));

    public static Task<string?> ExtendedPropertyAsync(TestDatabase db, string qualifiedTable, string? column)
        => db.ScalarAsync<string>(
            """
            SELECT CONVERT(nvarchar(max), ep.value)
            FROM sys.extended_properties ep
            WHERE ep.class = 1 AND ep.name = 'MS_Description' AND ep.major_id = OBJECT_ID(@table)
              AND ep.minor_id = CASE WHEN @column IS NULL THEN 0 ELSE (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND name = @column) END
            """,
            ("@table", qualifiedTable), ("@column", (object?)column ?? DBNull.Value));
}
