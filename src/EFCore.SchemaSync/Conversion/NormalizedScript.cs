namespace EFCore.SchemaSync.Conversion;

/// <summary>One declarative T-SQL statement destined for the DacFx model.</summary>
/// <param name="SourceName">Unique source name reported by DacFx in validation messages.</param>
/// <param name="Kind">Short description of the statement (for diagnostics).</param>
/// <param name="Text">The statement text, exactly as handed to DacFx.</param>
internal sealed record NormalizedStatement(string SourceName, string Kind, string Text);

/// <summary>Result of normalizing an EF Core create script.</summary>
internal sealed class NormalizedScript
{
    public NormalizedScript(IReadOnlyList<NormalizedStatement> statements, IReadOnlyList<string> warnings, int skippedSeedStatements)
    {
        Statements = statements;
        Warnings = warnings;
        SkippedSeedStatements = skippedSeedStatements;
    }

    public IReadOnlyList<NormalizedStatement> Statements { get; }

    public IReadOnlyList<string> Warnings { get; }

    public int SkippedSeedStatements { get; }

    /// <summary>All statements as one script with GO separators.</summary>
    public string ToScript()
        => string.Join(Environment.NewLine + "GO" + Environment.NewLine, Statements.Select(s => s.Text.TrimEnd())) + Environment.NewLine;
}
