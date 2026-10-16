using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync;

/// <summary>When a <see cref="SchemaSyncHook"/> runs.</summary>
public enum SchemaSyncHookPhase
{
    /// <summary>After the comparison, before any schema change is executed. The database still has the old schema.</summary>
    BeforeDeploy,

    /// <summary>After the schema changes were applied (or none were needed). The database matches the EF model.</summary>
    AfterDeploy,
}

/// <summary>
/// C# code that runs as part of a schema application, the EF-native alternative to SQL Server Data Tools
/// pre- and post-deployment scripts. Hooks run in registration order while the database-scoped schema lock is
/// held, so only one application instance runs them at a time. They run on every real application, including
/// when no schema change was needed, and never on a dry run, so they must be idempotent.
/// </summary>
/// <param name="Name">Name used in logs, errors and <see cref="SchemaSyncResult.ExecutedHooks"/>.</param>
/// <param name="Phase">When the hook runs.</param>
/// <param name="Callback">The code to run.</param>
public sealed record SchemaSyncHook(string Name, SchemaSyncHookPhase Phase, Func<SchemaSyncHookContext, CancellationToken, Task> Callback);

/// <summary>What a hook can see and use.</summary>
public sealed class SchemaSyncHookContext
{
    internal SchemaSyncHookContext(
        SchemaSyncHookPhase phase,
        DbContext dbContext,
        IServiceProvider? services,
        IReadOnlyList<SchemaChange> changes,
        SchemaSyncResult? result,
        string serverName,
        string databaseName,
        ILogger logger)
    {
        Phase = phase;
        DbContext = dbContext;
        Services = services;
        Changes = changes;
        Result = result;
        ServerName = serverName;
        DatabaseName = databaseName;
        Logger = logger;
    }

    /// <summary>The phase this hook runs in.</summary>
    public SchemaSyncHookPhase Phase { get; }

    /// <summary>
    /// The DbContext whose model is being applied, from the same scope. It uses the context's own connection
    /// (not <see cref="SchemaSyncOptions.DeploymentConnectionString"/>). In <see cref="SchemaSyncHookPhase.BeforeDeploy"/>
    /// the database still has the old schema, so only query what exists in both.
    /// </summary>
    public DbContext DbContext { get; }

    /// <summary>The scoped service provider the DbContext was resolved from; null when called without dependency injection.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>Planned changes (before deploy) or applied changes (after deploy). Empty when the schema was current.</summary>
    public IReadOnlyList<SchemaChange> Changes { get; }

    /// <summary>True when schema changes are about to be or were applied.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>The schema application result; set in <see cref="SchemaSyncHookPhase.AfterDeploy"/>, null before.</summary>
    public SchemaSyncResult? Result { get; }

    /// <summary>Data source of the target connection.</summary>
    public string ServerName { get; }

    /// <summary>Target database name.</summary>
    public string DatabaseName { get; }

    /// <summary>The library's logger (category <c>EFCore.SchemaSync</c>).</summary>
    public ILogger Logger { get; }

    /// <summary>The DbContext typed as <typeparamref name="TContext"/>.</summary>
    public TContext GetDbContext<TContext>()
        where TContext : DbContext
        => DbContext as TContext
            ?? throw new InvalidOperationException($"The hook runs for {DbContext.GetType().Name}, not {typeof(TContext).Name}.");
}
