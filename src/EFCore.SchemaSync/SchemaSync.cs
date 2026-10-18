using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EFCore.SchemaSync;

/// <summary>Entry point for applying an EF Core model to SQL Server without dependency injection.</summary>
public static class SchemaSync
{
    /// <summary>Logger category used by the library.</summary>
    public const string LoggerCategory = "EFCore.SchemaSync";

    /// <summary>
    /// Compares the model of <paramref name="context"/> with its database and applies the differences
    /// (or previews them when <see cref="SchemaSyncOptions.DryRun"/> is set).
    /// </summary>
    /// <exception cref="SchemaSyncException">The operation failed; <see cref="SchemaSyncException.Stage"/> names the stage.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static Task<SchemaSyncResult> ApplyAsync(DbContext context, SchemaSyncOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
        => SchemaSynchronizer.ApplyAsync(context, options ?? new SchemaSyncOptions(), logger ?? NullLogger.Instance, services: null, cancellationToken);

    /// <summary>
    /// Checks that the database of <paramref name="context"/> matches its model without changing anything.
    /// See <see cref="SchemaSyncServiceProviderExtensions.VerifyDatabaseSchemaAsync{TContext}(IServiceProvider, Action{SchemaSyncOptions}, CancellationToken)"/>.
    /// </summary>
    /// <exception cref="SchemaDriftException">The database differs from the model.</exception>
    public static async Task<SchemaSyncResult> VerifyAsync(DbContext context, Action<SchemaSyncOptions>? configure = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var result = await SchemaSynchronizer.ApplyAsync(context, VerificationOptions(configure), logger ?? NullLogger.Instance, services: null, cancellationToken).ConfigureAwait(false);
        return result.HasChanges ? throw new SchemaDriftException(result) : result;
    }

    internal static SchemaSyncOptions VerificationOptions(Action<SchemaSyncOptions>? configure)
    {
        var options = new SchemaSyncOptions();
        configure?.Invoke(options);
        options.DryRun = true;
        options.VerifyOnly = true;
        options.IncludeDeploymentScript = true;
        return options;
    }
}
