using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// Finds plan shapes that look like undeclared renames: a column missing from the model next to a new column of the
/// same base type in the same table, or a new table whose columns match a table the model no longer maps. Only
/// one-to-one matches produce a hint; anything ambiguous is left alone.
/// </summary>
internal static class RenameHintAnalyzer
{
    internal sealed record ModelColumn(string Name, string StoreType, string Suggestion);

    /// <summary>A table the model deploys.</summary>
    /// <param name="Schema">Schema name.</param>
    /// <param name="Name">Table name.</param>
    /// <param name="TableSuggestionPrefix">The entity builder expression for the table, or null when a table rename cannot be declared on it (owned collections).</param>
    /// <param name="Columns">Mapped columns with their store types and property suggestions.</param>
    internal sealed record ModelTable(string Schema, string Name, string? TableSuggestionPrefix, IReadOnlyList<ModelColumn> Columns);

    internal sealed record TargetColumn(string Name, string StoreType);

    internal sealed record TargetTable(string Schema, string Name, IReadOnlyList<TargetColumn> Columns);

    /// <summary>Describes the tables the context's model deploys, with the configuration line that declares a rename for each.</summary>
    /// <remarks>Uses the design-time model: the runtime model does not keep migration exclusions.</remarks>
    public static (IReadOnlyList<ModelTable> Deployed, IReadOnlySet<(string Schema, string Name)> Known) DescribeModel(DbContext context)
        => DescribeModel(context.GetService<IDesignTimeModel>().Model);

    /// <summary>Describes the tables a design-time EF model deploys.</summary>
    public static (IReadOnlyList<ModelTable> Deployed, IReadOnlySet<(string Schema, string Name)> Known) DescribeModel(IModel model)
    {
        var defaultSchema = model.GetDefaultSchema() ?? "dbo";
        var known = new HashSet<(string, string)>(TupleComparer.Instance);
        var tables = new Dictionary<(string, string), (string? Prefix, List<ModelColumn> Columns, HashSet<string> Names)>(TupleComparer.Instance);

        foreach (var entityType in model.GetEntityTypes())
        {
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            if (storeObject is null)
            {
                continue;
            }

            var table = storeObject.Value;
            var key = (table.Schema ?? defaultSchema, table.Name);
            known.Add(key);
            if (entityType.IsTableExcludedFromMigrations())
            {
                continue;
            }

            if (!tables.TryGetValue(key, out var entry))
            {
                var root = entityType.GetRootType();
                var prefix = root.FindOwnership() is null ? EntityPrefix(root) : null;
                entry = (prefix, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                tables[key] = entry;
            }

            foreach (var property in entityType.GetDeclaredProperties())
            {
                var column = property.GetColumnName(table);
                if (column is null || !entry.Names.Add(column))
                {
                    continue;
                }

                var storeType = property.GetColumnType(table) ?? string.Empty;
                entry.Columns.Add(new ModelColumn(column, storeType, PropertySuggestion(entityType, property)));
            }
        }

        var deployed = tables.Select(t => new ModelTable(t.Key.Item1, t.Key.Item2, t.Value.Prefix, t.Value.Columns)).ToList();
        return (deployed, known);
    }

    /// <summary>Reads every user table of the target database with its columns.</summary>
    public static async Task<IReadOnlyList<TargetTable>> ReadTargetAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name, t.name, c.name, TYPE_NAME(c.user_type_id)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tables = new Dictionary<(string, string), List<TargetColumn>>(TupleComparer.Instance);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!tables.TryGetValue(key, out var columns))
            {
                tables[key] = columns = [];
            }

            columns.Add(new TargetColumn(reader.GetString(2), reader.GetString(3)));
        }

        return tables.Select(t => new TargetTable(t.Key.Item1, t.Key.Item2, t.Value)).ToList();
    }

    /// <summary>Matches model and target into unambiguous rename hints.</summary>
    public static IReadOnlyList<RenameHint> Analyze(IReadOnlyList<ModelTable> model, IReadOnlySet<(string Schema, string Name)> known, IReadOnlyList<TargetTable> target)
    {
        var hints = new List<RenameHint>();
        var targetByName = target.ToDictionary(t => (t.Schema, t.Name), TupleComparer.Instance);

        // Columns: same table on both sides.
        foreach (var table in model)
        {
            if (!targetByName.TryGetValue((table.Schema, table.Name), out var existing))
            {
                continue;
            }

            var modelNames = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targetNames = existing.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dropped = existing.Columns.Where(c => !modelNames.Contains(c.Name)).ToList();
            var added = table.Columns.Where(c => !targetNames.Contains(c.Name)).ToList();

            foreach (var old in dropped)
            {
                var candidates = added.Where(a => SameBaseType(a.StoreType, old.StoreType)).ToList();
                if (candidates.Count != 1)
                {
                    continue;
                }

                var match = candidates[0];
                var competing = dropped.Count(d => SameBaseType(match.StoreType, d.StoreType));
                if (competing != 1)
                {
                    continue;
                }

                hints.Add(new RenameHint(
                    RenameHintKind.Column,
                    Qualify(existing.Schema, existing.Name, old.Name),
                    Qualify(table.Schema, table.Name, match.Name),
                    $"{match.Suggestion}.WasRenamedFrom(\"{Escape(old.Name)}\");"));
            }
        }

        // Tables: a new model table whose column set equals a target table the model does not know.
        var newTables = model.Where(t => !targetByName.ContainsKey((t.Schema, t.Name)) && t.TableSuggestionPrefix is not null).ToList();
        var orphans = target
            .Where(t => !known.Contains((t.Schema, t.Name)) && !IsInfrastructure(t))
            .ToList();
        foreach (var table in newTables)
        {
            var columns = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = orphans.Where(o => o.Columns.Count == columns.Count && o.Columns.All(c => columns.Contains(c.Name))).ToList();
            if (matches.Count != 1)
            {
                continue;
            }

            var old = matches[0];
            var reverse = newTables.Count(n => n.Columns.Count == old.Columns.Count && old.Columns.All(c => n.Columns.Any(m => string.Equals(m.Name, c.Name, StringComparison.OrdinalIgnoreCase))));
            if (reverse != 1)
            {
                continue;
            }

            var arguments = string.Equals(old.Schema, table.Schema, StringComparison.OrdinalIgnoreCase)
                ? $"\"{Escape(old.Name)}\""
                : $"\"{Escape(old.Name)}\", \"{Escape(old.Schema)}\"";
            hints.Add(new RenameHint(
                RenameHintKind.Table,
                Qualify(old.Schema, old.Name),
                Qualify(table.Schema, table.Name),
                $"{table.TableSuggestionPrefix}.WasRenamedFrom({arguments});"));
        }

        return hints;
    }

    internal static bool SameBaseType(string left, string right)
        => string.Equals(BaseType(left), BaseType(right), StringComparison.OrdinalIgnoreCase);

    internal static string BaseType(string storeType)
    {
        var trimmed = storeType.Trim();
        var paren = trimmed.IndexOf('(', StringComparison.Ordinal);
        var name = (paren >= 0 ? trimmed[..paren] : trimmed).Trim().Trim('[', ']').ToLowerInvariant();
        return name switch
        {
            "rowversion" => "timestamp",
            "dec" => "decimal",
            "double precision" => "float",
            "integer" => "int",
            "character varying" or "char varying" => "varchar",
            "national character varying" or "national char varying" => "nvarchar",
            _ => name,
        };
    }

    private static bool IsInfrastructure(TargetTable table)
        => string.Equals(table.Schema, "dbo", StringComparison.OrdinalIgnoreCase)
           && table.Name.StartsWith("__", StringComparison.Ordinal);

    private static string EntityPrefix(IEntityType entityType)
    {
        var ownership = entityType.FindOwnership();
        if (ownership is null)
        {
            return entityType.HasSharedClrType
                ? $"modelBuilder.SharedTypeEntity<{TypeName(entityType.ClrType)}>(\"{Escape(entityType.Name)}\")"
                : $"modelBuilder.Entity<{TypeName(entityType.ClrType)}>()";
        }

        // EF returns the existing owned builder when OwnsOne/OwnsMany is called again, so the chain is valid C#.
        var navigation = ownership.PrincipalToDependent?.Name ?? entityType.ShortName();
        var owns = ownership.IsUnique ? "OwnsOne" : "OwnsMany";
        return $"{EntityPrefix(ownership.PrincipalEntityType)}.{owns}(e => e.{navigation})";
    }

    private static string PropertySuggestion(IEntityType entityType, IProperty property)
    {
        var accessor = property.IsShadowProperty() || (property.PropertyInfo is null && property.FieldInfo is null)
            ? $"Property(\"{Escape(property.Name)}\")"
            : $"Property(e => e.{property.Name})";
        return $"{EntityPrefix(entityType)}.{accessor}";
    }

    private static string TypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }

    private static string Qualify(params string[] parts) => string.Join(".", parts.Select(p => "[" + p.Replace("]", "]]", StringComparison.Ordinal) + "]"));

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class TupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly TupleComparer Instance = new();

        public bool Equals((string, string) x, (string, string) y)
            => string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj)
            => HashCode.Combine(obj.Item1.ToUpperInvariant(), obj.Item2.ToUpperInvariant());
    }
}
