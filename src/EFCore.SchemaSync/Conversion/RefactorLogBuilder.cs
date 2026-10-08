using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.SchemaSync.Conversion;

/// <summary>
/// Derives refactor operations from the <c>WasRenamedFrom</c>/<c>WasMovedFromSchema</c> annotations of an EF model.
/// Column renames come first and reference the table under its previous name, then table renames, then schema moves,
/// so each operation names objects as they exist at that point in the sequence.
/// </summary>
internal static class RefactorLogBuilder
{
    private const string DefaultSchema = "dbo";

    public static RefactorLog FromModel(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var log = new RefactorLog();
        var defaultSchema = model.GetDefaultSchema() ?? DefaultSchema;

        var previousTables = new Dictionary<(string Schema, string Table), (string Schema, string Table)>();
        var tableOperations = new List<(string Schema, string Table, RefactorOperation Operation)>();
        var columnOperations = new List<(string Schema, string Table, string Column, RefactorOperation Operation)>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null)
            {
                continue;
            }

            var previousName = entityType.FindAnnotation(SchemaSyncModelBuilderExtensions.PreviousTableNameAnnotation)?.Value as string;
            var previousSchema = entityType.FindAnnotation(SchemaSyncModelBuilderExtensions.PreviousSchemaAnnotation)?.Value as string;
            if (previousName is null && previousSchema is null)
            {
                continue;
            }

            var schema = entityType.GetSchema() ?? defaultSchema;
            var oldSchema = previousSchema ?? schema;
            var oldName = previousName ?? tableName;
            if (string.Equals(oldSchema, schema, StringComparison.Ordinal) && string.Equals(oldName, tableName, StringComparison.Ordinal))
            {
                continue;
            }

            if (previousTables.ContainsKey((schema, tableName)))
            {
                continue; // several entity types share the table (owned types, TPH); the first annotation wins
            }

            previousTables[(schema, tableName)] = (oldSchema, oldName);
            if (!string.Equals(oldName, tableName, StringComparison.Ordinal))
            {
                tableOperations.Add((oldSchema, oldName, RefactorOperation.RenameTable(oldSchema, oldName, tableName)));
            }

            if (!string.Equals(oldSchema, schema, StringComparison.Ordinal))
            {
                tableOperations.Add((oldSchema, oldName, RefactorOperation.MoveToSchema(oldSchema, tableName, schema)));
            }
        }

        foreach (var entityType in model.GetEntityTypes())
        {
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            if (storeObject is null)
            {
                continue;
            }

            var table = storeObject.Value;
            var schema = table.Schema ?? defaultSchema;
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.FindAnnotation(SchemaSyncModelBuilderExtensions.PreviousColumnNameAnnotation)?.Value is not string previousColumn)
                {
                    continue;
                }

                var column = property.GetColumnName(table);
                if (column is null || string.Equals(column, previousColumn, StringComparison.Ordinal))
                {
                    continue;
                }

                var (oldSchema, oldTable) = previousTables.TryGetValue((schema, table.Name), out var previous) ? previous : (schema, table.Name);
                var isComputed = property.GetComputedColumnSql(table) is not null;
                columnOperations.Add((oldSchema, oldTable, previousColumn, RefactorOperation.RenameColumn(oldSchema, oldTable, previousColumn, column, isComputed)));
            }
        }

        // Deterministic order independent of how EF enumerates the model: columns (by table, then column),
        // then table renames and schema moves (by previous table name; rename before move per table),
        // then the keys, indexes and foreign keys whose EF-conventional names changed with them.
        log.AddRange(columnOperations
            .OrderBy(c => c.Schema, StringComparer.Ordinal).ThenBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.Column, StringComparer.Ordinal)
            .Select(c => c.Operation));
        log.AddRange(tableOperations
            .OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Table, StringComparer.Ordinal).ThenBy(t => t.Operation.IsMoveSchema ? 1 : 0)
            .Select(t => t.Operation));
        return log.AddRange(DependentRenames(model, defaultSchema, previousTables, columnOperations));
    }

    /// <summary>
    /// EF names keys, indexes and foreign keys after tables and columns (PK_Table, AK_Table_Cols, IX_Table_Cols,
    /// FK_Dependent_Principal_Cols). After a rename those names change too, and since target-only constraints
    /// are never dropped the old primary key would collide with the new one. Dependents whose current name
    /// follows the convention are therefore renamed in place as well; custom names are left untouched.
    /// </summary>
    private static IEnumerable<RefactorOperation> DependentRenames(
        IModel model,
        string defaultSchema,
        Dictionary<(string Schema, string Table), (string Schema, string Table)> previousTables,
        List<(string Schema, string Table, string Column, RefactorOperation Operation)> columnOperations)
    {
        // (new table identity) -> (new column -> old column)
        var previousColumns = new Dictionary<(string Schema, string Table), Dictionary<string, string>>();
        foreach (var (oldSchema, oldTable, oldColumn, operation) in columnOperations)
        {
            var current = previousTables.FirstOrDefault(p => p.Value == (oldSchema, oldTable));
            var key = current.Key == default ? (oldSchema, oldTable) : current.Key;
            if (!previousColumns.TryGetValue(key, out var map))
            {
                previousColumns[key] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            map[operation.GetProperty("NewName")!.Trim('[', ']').Replace("]]", "]", StringComparison.Ordinal)] = oldColumn;
        }

        var results = new List<(string Schema, string Table, string OldName, RefactorOperation Operation)>();
        var seen = new HashSet<Guid>();

        void Add(string schema, string table, string oldName, RefactorOperation operation)
        {
            if (seen.Add(operation.Key))
            {
                results.Add((schema, table, oldName, operation));
            }
        }

        string OldTableName((string Schema, string Table) table) => previousTables.TryGetValue(table, out var previous) ? previous.Table : table.Table;

        string OldColumn((string Schema, string Table) table, string column)
            => previousColumns.TryGetValue(table, out var map) && map.TryGetValue(column, out var old) ? old : column;

        foreach (var entityType in model.GetEntityTypes())
        {
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            if (storeObject is null)
            {
                continue;
            }

            var table = storeObject.Value;
            var identity = (table.Schema ?? defaultSchema, table.Name);
            var tableRenamed = previousTables.ContainsKey(identity);
            var columnsRenamed = previousColumns.ContainsKey(identity);

            foreach (var key in entityType.GetKeys())
            {
                var current = key.GetName(table);
                if (current is null || !(tableRenamed || columnsRenamed))
                {
                    continue;
                }

                var columns = key.Properties.Select(p => p.GetColumnName(table) ?? p.Name).ToList();
                var prefix = key.IsPrimaryKey() ? "PK" : "AK";
                var conventional = key.IsPrimaryKey() ? Conventional(prefix, table.Name, []) : Conventional(prefix, table.Name, columns);
                if (!string.Equals(current, conventional, StringComparison.Ordinal))
                {
                    continue;
                }

                var old = key.IsPrimaryKey()
                    ? Conventional(prefix, OldTableName(identity), [])
                    : Conventional(prefix, OldTableName(identity), columns.Select(c => OldColumn(identity, c)));
                if (!string.Equals(old, current, StringComparison.Ordinal))
                {
                    Add(identity.Item1, identity.Name, old, RefactorOperation.RenameConstraint(identity.Item1, identity.Name, key.IsPrimaryKey() ? RefactorConstraintKind.PrimaryKey : RefactorConstraintKind.Unique, old, current));
                }
            }

            foreach (var index in entityType.GetIndexes())
            {
                var current = index.GetDatabaseName(table);
                if (current is null || !(tableRenamed || columnsRenamed))
                {
                    continue;
                }

                var columns = index.Properties.Select(p => p.GetColumnName(table) ?? p.Name).ToList();
                if (!string.Equals(current, Conventional("IX", table.Name, columns), StringComparison.Ordinal))
                {
                    continue;
                }

                var old = Conventional("IX", OldTableName(identity), columns.Select(c => OldColumn(identity, c)));
                if (!string.Equals(old, current, StringComparison.Ordinal))
                {
                    Add(identity.Item1, identity.Name, old, RefactorOperation.RenameIndex(identity.Item1, identity.Name, old, current));
                }
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                var principalStoreObject = StoreObjectIdentifier.Create(foreignKey.PrincipalEntityType, StoreObjectType.Table);
                if (principalStoreObject is null)
                {
                    continue;
                }

                var principal = principalStoreObject.Value;
                var principalIdentity = (principal.Schema ?? defaultSchema, principal.Name);
                var principalRenamed = previousTables.ContainsKey(principalIdentity);
                if (!(tableRenamed || columnsRenamed || principalRenamed))
                {
                    continue;
                }

                var current = foreignKey.GetConstraintName(table, principal);
                if (current is null)
                {
                    continue;
                }

                var columns = foreignKey.Properties.Select(p => p.GetColumnName(table) ?? p.Name).ToList();
                if (!string.Equals(current, ConventionalForeignKey(table.Name, principal.Name, columns), StringComparison.Ordinal))
                {
                    continue;
                }

                var old = ConventionalForeignKey(OldTableName(identity), OldTableName(principalIdentity), columns.Select(c => OldColumn(identity, c)));
                if (!string.Equals(old, current, StringComparison.Ordinal))
                {
                    Add(identity.Item1, identity.Name, old, RefactorOperation.RenameConstraint(identity.Item1, identity.Name, RefactorConstraintKind.ForeignKey, old, current));
                }
            }
        }

        return results
            .OrderBy(r => r.Schema, StringComparer.Ordinal).ThenBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.OldName, StringComparer.Ordinal)
            .Select(r => r.Operation);
    }

    private static string Conventional(string prefix, string table, IEnumerable<string> columns)
    {
        var suffix = string.Join("_", columns);
        return suffix.Length == 0 ? $"{prefix}_{table}" : $"{prefix}_{table}_{suffix}";
    }

    private static string ConventionalForeignKey(string dependentTable, string principalTable, IEnumerable<string> columns)
        => $"FK_{dependentTable}_{principalTable}_{string.Join("_", columns)}";
}
