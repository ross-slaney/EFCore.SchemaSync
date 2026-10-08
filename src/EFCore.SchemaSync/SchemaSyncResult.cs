using System.Text;

namespace EFCore.SchemaSync;

/// <summary>What the schema application did.</summary>
public enum SchemaSyncOutcome
{
    /// <summary>The database already matched the EF model; nothing was executed.</summary>
    NoChangesNeeded,

    /// <summary>Changes were found and scripted but not executed (<see cref="SchemaSyncOptions.DryRun"/>).</summary>
    Previewed,

    /// <summary>Changes were deployed to the database.</summary>
    Applied,
}

/// <summary>Kind of operation DacFx planned for a database object.</summary>
public enum SchemaChangeKind
{
    /// <summary>The object is created.</summary>
    Create,

    /// <summary>The object is altered in place.</summary>
    Alter,

    /// <summary>The object is dropped.</summary>
    Drop,

    /// <summary>The table is rebuilt (new table, data copied, old table dropped) to apply changes ALTER TABLE cannot express.</summary>
    TableRebuild,

    /// <summary>Any other DacFx operation (for example a module refresh); see <see cref="SchemaChange.Operation"/>.</summary>
    Other,

    /// <summary>The object is renamed in place (<c>sp_rename</c>) because of a refactor log entry.</summary>
    Rename,

    /// <summary>The object is transferred to another schema (<c>ALTER SCHEMA ... TRANSFER</c>) because of a refactor log entry.</summary>
    MoveSchema,
}

/// <summary>One planned or applied schema operation.</summary>
/// <param name="Kind">Operation category.</param>
/// <param name="ObjectType">DacFx object type without the "Sql" prefix, for example "Table", "Index", "ForeignKeyConstraint".</param>
/// <param name="ObjectName">Qualified object name, for example "[dbo].[Customers]".</param>
/// <param name="Operation">Raw DacFx operation name, for example "Create", "Alter", "Drop", "TableRebuild".</param>
public sealed record SchemaChange(SchemaChangeKind Kind, string ObjectType, string ObjectName, string Operation)
{
    /// <inheritdoc />
    public override string ToString() => $"{Operation} {ObjectType} {ObjectName}";
}

/// <summary>A database object referenced in a result.</summary>
/// <param name="ObjectType">DacFx object type without the "Sql" prefix.</param>
/// <param name="Name">Qualified object name.</param>
public sealed record SchemaObjectReference(string ObjectType, string Name)
{
    /// <inheritdoc />
    public override string ToString() => $"{ObjectType} {Name}";
}

/// <summary>Structured result of a schema application.</summary>
public sealed class SchemaSyncResult
{
    internal SchemaSyncResult(
        SchemaSyncOutcome outcome,
        string contextName,
        string serverName,
        string databaseName,
        IReadOnlyList<SchemaChange> changes,
        IReadOnlyList<SchemaObjectReference> retainedObjects,
        IReadOnlyList<string> dataLossRisks,
        IReadOnlyList<string> warnings,
        string? deploymentScript,
        TimeSpan duration)
    {
        Outcome = outcome;
        ContextName = contextName;
        ServerName = serverName;
        DatabaseName = databaseName;
        Changes = changes;
        RetainedObjects = retainedObjects;
        DataLossRisks = dataLossRisks;
        Warnings = warnings;
        DeploymentScript = deploymentScript;
        Duration = duration;
    }

    /// <summary>Whether changes were unnecessary, previewed or applied.</summary>
    public SchemaSyncOutcome Outcome { get; }

    /// <summary>Name of the DbContext type whose model was applied.</summary>
    public string ContextName { get; }

    /// <summary>Data source of the target connection (no credentials).</summary>
    public string ServerName { get; }

    /// <summary>Target database name.</summary>
    public string DatabaseName { get; }

    /// <summary>Operations DacFx planned (dry run) or executed (applied). Empty when no changes were needed.</summary>
    public IReadOnlyList<SchemaChange> Changes { get; }

    /// <summary>
    /// Objects that exist in the database but not in the EF model and were preserved because
    /// <see cref="SchemaSyncOptions.AllowObjectRemoval"/> is off (or the type is never dropped).
    /// </summary>
    public IReadOnlyList<SchemaObjectReference> RetainedObjects { get; }

    /// <summary>
    /// DacFx data-loss warnings for the planned changes. Non-empty only when <see cref="SchemaSyncOptions.AllowDataLoss"/>
    /// was set (otherwise the operation throws <see cref="SchemaChangesBlockedException"/>) or on a dry run.
    /// </summary>
    public IReadOnlyList<string> DataLossRisks { get; }

    /// <summary>Warnings produced during conversion, comparison and deployment.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// The DacFx deployment script (SQLCMD syntax). Set on dry runs, and on applied deployments when
    /// <see cref="SchemaSyncOptions.IncludeDeploymentScript"/> is true.
    /// </summary>
    public string? DeploymentScript { get; }

    /// <summary>Wall-clock time of the whole operation, including lock wait.</summary>
    public TimeSpan Duration { get; }

    /// <summary>True when at least one schema operation was planned or applied.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>Human-readable multi-line summary suitable for logs and console output.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(Outcome switch
        {
            SchemaSyncOutcome.NoChangesNeeded => "Schema is current",
            SchemaSyncOutcome.Previewed => $"Dry run: {Changes.Count} change(s) previewed",
            SchemaSyncOutcome.Applied => $"Applied {Changes.Count} change(s)",
            _ => Outcome.ToString(),
        });
        sb.Append(" for ").Append(ContextName).Append(" on ").Append(ServerName).Append('/').Append(DatabaseName)
          .Append(" in ").Append(Duration.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("s.");

        foreach (var change in Changes)
        {
            sb.AppendLine().Append("  ").Append(change);
        }

        if (RetainedObjects.Count > 0)
        {
            sb.AppendLine().Append("  Retained target-only objects (not in the EF model, preserved):");
            foreach (var retained in RetainedObjects)
            {
                sb.AppendLine().Append("    ").Append(retained);
            }
        }

        foreach (var risk in DataLossRisks)
        {
            sb.AppendLine().Append("  Data loss risk: ").Append(risk);
        }

        foreach (var warning in Warnings)
        {
            sb.AppendLine().Append("  Warning: ").Append(warning);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public override string ToString() => Describe();
}
