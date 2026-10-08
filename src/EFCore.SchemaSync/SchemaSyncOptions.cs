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

        if (string.IsNullOrWhiteSpace(LockResourceName) || LockResourceName.Length > 255)
        {
            throw new ArgumentException("LockResourceName must be 1 to 255 characters.", nameof(LockResourceName));
        }
    }
}
