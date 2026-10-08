using Microsoft.EntityFrameworkCore;

namespace EFCore.SchemaSync.Conversion;

/// <summary>
/// Converts an EF Core model into a validated DacFx DACPAC without touching any database:
/// EF Core generates its create script, the script is parsed with ScriptDom and normalized into declarative
/// statements, DacFx builds and validates the model, and the DACPAC is produced in memory.
/// </summary>
public static class SchemaConverter
{
    /// <summary>Converts the model of <paramref name="context"/> into a DACPAC.</summary>
    /// <exception cref="SchemaSyncException">The context is not configured for SQL Server, EF Core could not generate the script, or DacFx rejected the model.</exception>
    /// <exception cref="UnsupportedSchemaException">The model emits constructs the library cannot deploy declaratively.</exception>
    public static SchemaPackage Convert(DbContext context, SchemaConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        options ??= new SchemaConversionOptions();

        if (!context.Database.IsSqlServer())
        {
            throw new SchemaSyncException(
                SchemaSyncStage.ResolveContext,
                $"{context.GetType().Name} is configured for provider '{context.Database.ProviderName ?? "(none)"}'. EFCore.SchemaSync supports Microsoft.EntityFrameworkCore.SqlServer only.");
        }

        string createScript;
        try
        {
            createScript = context.Database.GenerateCreateScript();
        }
        catch (Exception ex) when (ex is not SchemaSyncException)
        {
            throw new SchemaSyncException(SchemaSyncStage.ConvertModel, $"EF Core could not generate the create script for {context.GetType().Name}: {ex.Message}", ex);
        }

        var refactorLog = options.UseModelAnnotations ? RefactorLogBuilder.FromModel(context.Model) : new RefactorLog();
        return ConvertScript(createScript, options.PackageName ?? context.GetType().Name, options, refactorLog);
    }

    /// <summary>
    /// Converts an EF Core create script (the output of <c>DbContext.Database.GenerateCreateScript()</c> for the
    /// SQL Server provider) into a DACPAC. Exposed for tests and diagnostics.
    /// </summary>
    public static SchemaPackage ConvertScript(string createScript, string packageName, SchemaConversionOptions? options = null)
        => ConvertScript(createScript, packageName, options ?? new SchemaConversionOptions(), new RefactorLog());

    private static SchemaPackage ConvertScript(string createScript, string packageName, SchemaConversionOptions options, RefactorLog refactorLog)
    {
        ArgumentNullException.ThrowIfNull(createScript);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        if (options.RefactorLog is { } explicitLog)
        {
            refactorLog.AddRange(explicitLog.Operations);
        }

        if (!string.IsNullOrWhiteSpace(options.RefactorLogPath))
        {
            try
            {
                refactorLog.AddRange(RefactorLog.Load(options.RefactorLogPath).Operations);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException)
            {
                throw new SchemaSyncException(SchemaSyncStage.ConvertModel, $"The refactor log '{options.RefactorLogPath}' could not be read: {ex.Message}", ex);
            }
        }

        var normalized = SqlServerScriptNormalizer.Normalize(createScript);
        return DacpacBuilder.Build(normalized, packageName, options.TargetSqlServerVersion, options.Collation, refactorLog);
    }
}
