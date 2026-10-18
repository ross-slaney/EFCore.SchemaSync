namespace EFCore.SchemaSync.Conversion;

/// <summary>
/// The cheap first half of a conversion: EF's create script and the merged refactor log. Building the DACPAC
/// (ScriptDom normalization, DacFx model, validation) is deferred to <see cref="Build"/> so it can be skipped.
/// </summary>
internal sealed class PreparedSchema
{
    private readonly SchemaConversionOptions _options;

    public PreparedSchema(string createScript, RefactorLog refactorLog, string packageName, SchemaConversionOptions options)
    {
        CreateScript = createScript;
        RefactorLog = refactorLog;
        PackageName = packageName;
        _options = options;
    }

    public string CreateScript { get; }

    public RefactorLog RefactorLog { get; }

    public string PackageName { get; }

    /// <summary>Normalizes the script and builds the DACPAC, optionally with extra library-owned statements.</summary>
    public SchemaPackage Build(IReadOnlyList<NormalizedStatement>? extraStatements = null)
    {
        var normalized = SqlServerScriptNormalizer.Normalize(CreateScript);
        if (extraStatements is { Count: > 0 })
        {
            normalized = new NormalizedScript([.. extraStatements, .. normalized.Statements], normalized.Warnings, normalized.SkippedSeedStatements);
        }

        return DacpacBuilder.Build(normalized, PackageName, _options.TargetSqlServerVersion, _options.Collation, RefactorLog);
    }
}
