using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Model;

namespace EFCore.SchemaSync;

/// <summary>
/// Controls how <see cref="SchemaSyncServiceProviderExtensions.ApplyDatabaseSchemaAsync{TContext}(IServiceProvider, SchemaSyncOptions, CancellationToken)"/>
/// compares and deploys the EF Core model. The defaults are the safe ones: no object removal, no data loss,
/// transactional deployment, DacFx verification on, security and database settings unmanaged.
/// </summary>
public sealed class SchemaSyncOptions
{
    /// <summary>
    /// When true, the schema is compared and the deployment script is generated but nothing is executed
    /// against the target database. The result has <see cref="SchemaSyncOutcome.Previewed"/>
    /// (or <see cref="SchemaSyncOutcome.NoChangesNeeded"/>) and carries the script in
    /// <see cref="SchemaSyncResult.DeploymentScript"/>.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Connection string used for the lock, comparison and deployment instead of the one configured on the
    /// DbContext. Use it to deploy with elevated (DDL) credentials while the application itself runs with
    /// least privilege. Must include an initial catalog (database name).
    /// </summary>
    public string? DeploymentConnectionString { get; set; }

    /// <summary>
    /// Maximum time to wait for the database-scoped schema lock held by another instance that is currently
    /// comparing or deploying. Exceeding it throws <see cref="SchemaLockTimeoutException"/>. Default: 2 minutes.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Timeout for each individual command DacFx executes during comparison and deployment. Default: 5 minutes.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Timeout DacFx uses for long-running commands (for example index creation and table rebuilds).
    /// When null, <see cref="CommandTimeout"/> applies.
    /// </summary>
    public TimeSpan? LongRunningCommandTimeout { get; set; }

    /// <summary>
    /// Opt in to dropping objects that exist in the database but not in the EF model: tables, views,
    /// procedures, functions, triggers, indexes, constraints, sequences, schemas and extended properties.
    /// Off by default: target-only objects are preserved and reported in <see cref="SchemaSyncResult.RetainedObjects"/>.
    /// Dropping a table that contains rows additionally requires <see cref="AllowDataLoss"/>.
    /// </summary>
    public bool AllowObjectRemoval { get; set; }

    /// <summary>
    /// Opt in to changes DacFx flags as potentially lossy: dropping or narrowing columns, dropping tables
    /// with rows, type changes that cannot preserve data. Off by default: such changes throw
    /// <see cref="SchemaChangesBlockedException"/> before anything is executed.
    /// </summary>
    public bool AllowDataLoss { get; set; }

    /// <summary>
    /// Schemas this DbContext owns when it shares the database with other contexts or applications. When set, only
    /// target-only objects in these schemas (plus every schema the model deploys into) are reported as retained or
    /// dropped by <see cref="AllowObjectRemoval"/>; everything in other schemas is left alone entirely. Empty (the
    /// default) means the context owns the whole database. Tables the EF model maps but excludes from migrations
    /// (<c>ExcludeFromMigrations()</c>) are always left alone, whatever this setting.
    /// </summary>
    public ICollection<string> ManagedSchemas { get; } = new List<string>();

    /// <summary>
    /// Skip the DacFx comparison on a start where nothing changed. After every successful application the library
    /// stores a hash of the model and a fingerprint of the schema in <c>dbo.__SchemaSyncState</c>; when both still
    /// match, DacFx is not invoked and the result reports <see cref="SchemaSyncOutcome.NoChangesNeeded"/> with
    /// <see cref="SchemaSyncResult.ComparisonSkipped"/> set. Any model change, deployment-option change, library upgrade
    /// or DDL against the database (by anyone) runs the full comparison again. Dry runs always compare, and so does every
    /// start while <see cref="ConfigureDeployOptions"/> is set (its effect cannot be hashed). Default: true.
    /// </summary>
    public bool SkipComparisonWhenUnchanged { get; set; } = true;

    /// <summary>
    /// Set by the verification APIs: a read-only dry run that may answer from the recorded fingerprint and never runs hooks.
    /// </summary>
    internal bool VerifyOnly { get; set; }

    /// <summary>
    /// Compute and report database objects that only exist in the target and were preserved
    /// (<see cref="SchemaSyncResult.RetainedObjects"/>). This costs one additional comparison pass.
    /// Default: true.
    /// </summary>
    public bool ReportRetainedObjects { get; set; } = true;

    /// <summary>
    /// Wrap the deployment in a transaction so a failed statement rolls back the schema changes made
    /// before it. SQL Server cannot roll back every DDL operation (for example some full-text and
    /// memory-optimized operations), so this is best effort rather than a guarantee. Default: true.
    /// </summary>
    public bool UseTransaction { get; set; } = true;

    /// <summary>
    /// Also generate and return the deployment script when changes are applied (not only on dry run).
    /// Default: false.
    /// </summary>
    public bool IncludeDeploymentScript { get; set; }

    /// <summary>
    /// SQL Server platform the generated DACPAC targets. Default: <see cref="SqlServerVersion.Sql160"/> (SQL Server 2022).
    /// Deploying to a newer engine is allowed; deploying to an older or different platform requires
    /// <see cref="AllowIncompatiblePlatform"/>.
    /// </summary>
    public SqlServerVersion TargetSqlServerVersion { get; set; } = SqlServerVersion.Sql160;

    /// <summary>
    /// Let DacFx deploy even when the target platform differs from <see cref="TargetSqlServerVersion"/>
    /// (for example Azure SQL Database). Default: false.
    /// </summary>
    public bool AllowIncompatiblePlatform { get; set; }

    /// <summary>
    /// Name of the <c>sp_getapplock</c> resource (scoped to the target database) that serializes schema
    /// application across application instances. Default: "EFCore.SchemaSync".
    /// </summary>
    public string LockResourceName { get; set; } = "EFCore.SchemaSync";

    /// <summary>
    /// Escape hatch invoked after the library has configured the DacFx deployment options, before they
    /// are used. Settings changed here override the library defaults; use with care.
    /// </summary>
    public Action<DacDeployOptions>? ConfigureDeployOptions { get; set; }

    /// <summary>
    /// Explicit renames and schema moves DacFx applies in place (<c>sp_rename</c>, <c>ALTER SCHEMA ... TRANSFER</c>)
    /// instead of dropping and recreating objects. Applied after the operations derived from the model's
    /// <c>WasRenamedFrom</c> annotations. Each operation runs once; DacFx records its key in <c>dbo.__RefactorLog</c>.
    /// </summary>
    public RefactorLog? RefactorLog { get; set; }

    /// <summary>Path of an SSDT-style <c>.refactorlog</c> file whose operations are applied last.</summary>
    public string? RefactorLogPath { get; set; }

    /// <summary>Registered hooks in registration order. Use <see cref="BeforeDeploy(string, Func{SchemaSyncHookContext, CancellationToken, Task})"/> and <see cref="AfterDeploy(string, Func{SchemaSyncHookContext, CancellationToken, Task})"/> to add them.</summary>
    public IList<SchemaSyncHook> Hooks { get; } = new List<SchemaSyncHook>();

    /// <summary>
    /// Registers C# code that runs after the comparison and before any schema change, while the schema lock is held
    /// (for example to fix data so a change can succeed). Runs on every real application, never on a dry run, and
    /// not when the deployment is blocked. Must be idempotent.
    /// </summary>
    public SchemaSyncOptions BeforeDeploy(Func<SchemaSyncHookContext, CancellationToken, Task> hook)
        => AddHook(null, SchemaSyncHookPhase.BeforeDeploy, hook);

    /// <inheritdoc cref="BeforeDeploy(Func{SchemaSyncHookContext, CancellationToken, Task})"/>
    public SchemaSyncOptions BeforeDeploy(string name, Func<SchemaSyncHookContext, CancellationToken, Task> hook)
        => AddHook(name, SchemaSyncHookPhase.BeforeDeploy, hook);

    /// <summary>
    /// Registers C# code that runs after the schema matches the EF model, while the schema lock is held (for example
    /// to seed reference data with the DbContext). Runs on every real application, including when no change was
    /// needed, and never on a dry run. Must be idempotent.
    /// </summary>
    public SchemaSyncOptions AfterDeploy(Func<SchemaSyncHookContext, CancellationToken, Task> hook)
        => AddHook(null, SchemaSyncHookPhase.AfterDeploy, hook);

    /// <inheritdoc cref="AfterDeploy(Func{SchemaSyncHookContext, CancellationToken, Task})"/>
    public SchemaSyncOptions AfterDeploy(string name, Func<SchemaSyncHookContext, CancellationToken, Task> hook)
        => AddHook(name, SchemaSyncHookPhase.AfterDeploy, hook);

    private SchemaSyncOptions AddHook(string? name, SchemaSyncHookPhase phase, Func<SchemaSyncHookContext, CancellationToken, Task> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        }

        var number = Hooks.Count(h => h.Phase == phase) + 1;
        Hooks.Add(new SchemaSyncHook(name ?? $"{phase} #{number}", phase, hook));
        return this;
    }

    internal void Validate()
    {
        if (LockTimeout <= TimeSpan.Zero || LockTimeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(LockTimeout), "LockTimeout must be between 1 millisecond and 1 day.");
        }

        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout), "CommandTimeout must be positive.");
        }

        if (LongRunningCommandTimeout is { } longRunning && longRunning <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LongRunningCommandTimeout), "LongRunningCommandTimeout must be positive when set.");
        }

        if (Hooks.Any(h => h is null || h.Callback is null || string.IsNullOrWhiteSpace(h.Name)))
        {
            throw new ArgumentException("Every hook needs a name and a callback.", nameof(Hooks));
        }

        if (string.IsNullOrWhiteSpace(LockResourceName) || LockResourceName.Length > 255)
        {
            throw new ArgumentException("LockResourceName must be 1 to 255 characters.", nameof(LockResourceName));
        }
    }
}
