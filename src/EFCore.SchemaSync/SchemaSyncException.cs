namespace EFCore.SchemaSync;

/// <summary>Stage of the schema application at which a failure occurred.</summary>
public enum SchemaSyncStage
{
    /// <summary>Resolving the DbContext, validating the provider and reading its connection configuration.</summary>
    ResolveContext,

    /// <summary>Generating the EF Core create script and normalizing it into declarative T-SQL.</summary>
    ConvertModel,

    /// <summary>Building and validating the DacFx model and DACPAC.</summary>
    BuildPackage,

    /// <summary>Opening the connection to the target database.</summary>
    Connect,

    /// <summary>Acquiring the database-scoped schema lock.</summary>
    AcquireLock,

    /// <summary>Comparing the DACPAC with the live database and planning the deployment.</summary>
    Compare,

    /// <summary>Executing the deployment against the database.</summary>
    Deploy,
}

/// <summary>
/// Base exception for every failure raised by EFCore.SchemaSync. <see cref="Stage"/> identifies where
/// the operation failed and <see cref="Exception.InnerException"/> retains the underlying cause
/// (DacFx, SqlClient or EF Core exception) when there is one.
/// </summary>
public class SchemaSyncException : Exception
{
    /// <summary>Creates an exception for the given stage.</summary>
    public SchemaSyncException(SchemaSyncStage stage, string message, Exception? innerException = null)
        : base($"{message} (stage: {stage})", innerException)
    {
        Stage = stage;
    }

    /// <summary>Stage at which the failure occurred.</summary>
    public SchemaSyncStage Stage { get; }
}

/// <summary>
/// The EF model uses mappings or emits statements that EFCore.SchemaSync does not support. Nothing was
/// deployed. <see cref="UnsupportedConstructs"/> lists every offending construct.
/// </summary>
public sealed class UnsupportedSchemaException : SchemaSyncException
{
    /// <summary>Creates the exception from the list of unsupported constructs.</summary>
    public UnsupportedSchemaException(IReadOnlyList<string> unsupportedConstructs)
        : base(SchemaSyncStage.ConvertModel, BuildMessage(unsupportedConstructs))
    {
        UnsupportedConstructs = unsupportedConstructs;
    }

    /// <summary>Descriptions of the unsupported constructs found in the model's create script.</summary>
    public IReadOnlyList<string> UnsupportedConstructs { get; }

    private static string BuildMessage(IReadOnlyList<string> constructs)
        => "The EF model contains constructs EFCore.SchemaSync cannot deploy declaratively. "
         + "Remove them from the model or manage them outside EFCore.SchemaSync: "
         + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", constructs);
}

/// <summary>The database-scoped schema lock could not be acquired within <see cref="SchemaSyncOptions.LockTimeout"/>.</summary>
public sealed class SchemaLockTimeoutException : SchemaSyncException
{
    /// <summary>Creates the exception.</summary>
    public SchemaLockTimeoutException(string resourceName, TimeSpan timeout)
        : base(SchemaSyncStage.AcquireLock,
            $"Could not acquire the schema lock '{resourceName}' within {timeout}. Another instance is applying the schema; retry later or increase SchemaSyncOptions.LockTimeout.")
    {
        ResourceName = resourceName;
        Timeout = timeout;
    }

    /// <summary>The application lock resource name.</summary>
    public string ResourceName { get; }

    /// <summary>The wait that elapsed.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// DacFx flagged the planned changes as potentially lossy and <see cref="SchemaSyncOptions.AllowDataLoss"/>
/// is off. Nothing was executed. <see cref="DataLossRisks"/> lists the DacFx data issues.
/// </summary>
public sealed class SchemaChangesBlockedException : SchemaSyncException
{
    /// <summary>Creates the exception from DacFx data-loss issues.</summary>
    public SchemaChangesBlockedException(IReadOnlyList<string> dataLossRisks, IReadOnlyList<SchemaChange> plannedChanges)
        : base(SchemaSyncStage.Compare, BuildMessage(dataLossRisks))
    {
        DataLossRisks = dataLossRisks;
        PlannedChanges = plannedChanges;
    }

    /// <summary>DacFx descriptions of the lossy operations.</summary>
    public IReadOnlyList<string> DataLossRisks { get; }

    /// <summary>The full set of operations DacFx planned, including the lossy ones.</summary>
    public IReadOnlyList<SchemaChange> PlannedChanges { get; }

    private static string BuildMessage(IReadOnlyList<string> risks)
        => "Deployment blocked: the planned schema changes may cause data loss and SchemaSyncOptions.AllowDataLoss is false. "
         + "Review the changes and set AllowDataLoss = true to apply them:"
         + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", risks);
}
