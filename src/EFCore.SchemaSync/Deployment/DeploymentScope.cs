using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// Which part of the database a context owns. Objects outside it are never reported as retained and never dropped:
/// objects in schemas outside <see cref="ManagedSchemas"/> (when set), and tables the EF model maps but excludes from
/// migrations, together with everything that hangs off them. Enforced inside DacFx by <see cref="ScopeContributor"/>.
/// </summary>
internal sealed class DeploymentScope
{
    private static readonly ConcurrentDictionary<string, DeploymentScope> Registry = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _removed = new(StringComparer.OrdinalIgnoreCase);

    public DeploymentScope(IReadOnlySet<string>? managedSchemas, IReadOnlySet<string> unmanagedTables)
    {
        ManagedSchemas = managedSchemas;
        UnmanagedTables = unmanagedTables;
    }

    /// <summary>Schemas the context owns; null means the whole database.</summary>
    public IReadOnlySet<string>? ManagedSchemas { get; }

    /// <summary>Qualified names (<c>[schema].[table]</c>) of tables the model maps but does not own.</summary>
    public IReadOnlySet<string> UnmanagedTables { get; }

    /// <summary>True when part of the database is out of scope, so DacFx needs the scope contributor.</summary>
    public bool IsRestricted => ManagedSchemas is not null || UnmanagedTables.Count > 0;

    /// <summary>Qualified names of objects whose drop the contributor removed from a plan.</summary>
    public IReadOnlyCollection<string> Removed => _removed.Keys.ToArray();

    /// <summary>Builds the scope from the options and the context's design-time model.</summary>
    public static DeploymentScope Create(DbContext context, SchemaSyncOptions options)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var defaultSchema = model.GetDefaultSchema() ?? "dbo";
        var unmanaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in model.GetEntityTypes())
        {
            var table = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            if (table is null)
            {
                continue;
            }

            var schema = table.Value.Schema ?? defaultSchema;
            if (entityType.IsTableExcludedFromMigrations())
            {
                unmanaged.Add(Qualify(schema, table.Value.Name));
            }
            else
            {
                modelSchemas.Add(schema);
            }
        }

        foreach (var sequence in model.GetSequences())
        {
            modelSchemas.Add(sequence.Schema ?? defaultSchema);
        }

        // Tables the model deploys are managed even if another entity type excludes the same table.
        foreach (var entityType in model.GetEntityTypes().Where(e => !e.IsTableExcludedFromMigrations()))
        {
            if (StoreObjectIdentifier.Create(entityType, StoreObjectType.Table) is { } table)
            {
                unmanaged.Remove(Qualify(table.Schema ?? defaultSchema, table.Name));
            }
        }

        HashSet<string>? managed = null;
        if (options.ManagedSchemas.Count > 0)
        {
            managed = new HashSet<string>(options.ManagedSchemas.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
            managed.UnionWith(modelSchemas);
        }

        return new DeploymentScope(managed, unmanaged);
    }

    /// <summary>True when a drop of an object in <paramref name="schema"/> (owned by <paramref name="owningTable"/>, if any) must be kept out of the plan.</summary>
    public bool IsOutOfScope(string? schema, string? owningTable)
        => (ManagedSchemas is not null && schema is not null && !ManagedSchemas.Contains(schema))
           || (owningTable is not null && UnmanagedTables.Contains(owningTable));

    internal void RecordRemoved(string qualifiedName) => _removed.TryAdd(qualifiedName, 0);

    /// <summary>Makes the scope reachable from the contributor DacFx instantiates; dispose to unregister.</summary>
    public Registration Register()
    {
        var token = Guid.NewGuid().ToString("N");
        Registry[token] = this;
        return new Registration(token);
    }

    internal static DeploymentScope? Find(string token) => Registry.TryGetValue(token, out var scope) ? scope : null;

    internal static string Qualify(string schema, string name) => $"[{schema.Replace("]", "]]", StringComparison.Ordinal)}].[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    internal sealed class Registration(string token) : IDisposable
    {
        public string Token { get; } = token;

        public void Dispose() => Registry.TryRemove(Token, out _);
    }
}
