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
        => SchemaSynchronizer.ApplyAsync(context, options ?? new SchemaSyncOptions(), logger ?? NullLogger.Instance, cancellationToken);
}
