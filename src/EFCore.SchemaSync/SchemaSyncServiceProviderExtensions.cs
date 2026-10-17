using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EFCore.SchemaSync;

/// <summary>
/// Applies an EF Core model to its SQL Server database at startup:
/// <code>
/// var app = builder.Build();
/// await app.Services.ApplyDatabaseSchemaAsync&lt;AppDbContext&gt;();
/// await app.RunAsync();
/// </code>
/// </summary>
public static class SchemaSyncServiceProviderExtensions
{
    /// <summary>
    /// Resolves <typeparamref name="TContext"/> from a new scope, converts its model to a DACPAC, compares it with the
    /// database and applies the differences with the default (safe) options. Throws on failure so startup stops.
    /// </summary>
    public static Task<SchemaSyncResult> ApplyDatabaseSchemaAsync<TContext>(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        where TContext : DbContext
        => ApplyDatabaseSchemaAsync<TContext>(serviceProvider, new SchemaSyncOptions(), cancellationToken);

    /// <summary>Same as the parameterless overload, with options configured inline.</summary>
    public static Task<SchemaSyncResult> ApplyDatabaseSchemaAsync<TContext>(this IServiceProvider serviceProvider, Action<SchemaSyncOptions> configure, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new SchemaSyncOptions();
        configure(options);
        return ApplyDatabaseSchemaAsync<TContext>(serviceProvider, options, cancellationToken);
    }

    /// <summary>
    /// Resolves <typeparamref name="TContext"/> from a new scope, converts its model to a DACPAC, compares it with the
    /// database and applies (or previews) the differences according to <paramref name="options"/>.
    /// </summary>
    /// <exception cref="SchemaSyncException">The operation failed; <see cref="SchemaSyncException.Stage"/> names the stage.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static async Task<SchemaSyncResult> ApplyDatabaseSchemaAsync<TContext>(this IServiceProvider serviceProvider, SchemaSyncOptions options, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);

        var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(SchemaSync.LoggerCategory) ?? NullLogger.Instance;

        await using var scope = serviceProvider.CreateAsyncScope();
        TContext context;
        try
        {
            context = scope.ServiceProvider.GetRequiredService<TContext>();
        }
        catch (InvalidOperationException ex)
        {
            throw new SchemaSyncException(SchemaSyncStage.ResolveContext, $"Could not resolve {typeof(TContext).Name} from the service provider: {ex.Message}", ex);
        }

        return await SchemaSynchronizer.ApplyAsync(context, options, logger, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks that the database matches the model of <typeparamref name="TContext"/> without changing anything: a dry run
    /// that runs no hooks and writes no state. When the schema is unchanged since the last application with the same
    /// options, the recorded fingerprint answers without DacFx; otherwise DacFx compares in full. Target-only objects
    /// that would be retained do not count as drift.
    /// </summary>
    /// <returns>The result when the database is current.</returns>
    /// <exception cref="SchemaDriftException">The database differs from the model.</exception>
    /// <exception cref="SchemaSyncException">The check itself failed; <see cref="SchemaSyncException.Stage"/> names the stage.</exception>
    public static Task<SchemaSyncResult> VerifyDatabaseSchemaAsync<TContext>(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        where TContext : DbContext
        => VerifyDatabaseSchemaAsync<TContext>(serviceProvider, static _ => { }, cancellationToken);

    /// <summary>
    /// Same as <see cref="VerifyDatabaseSchemaAsync{TContext}(IServiceProvider, CancellationToken)"/>, configured with the
    /// options the application uses at startup (so the recorded fingerprint can answer).
    /// </summary>
    public static async Task<SchemaSyncResult> VerifyDatabaseSchemaAsync<TContext>(this IServiceProvider serviceProvider, Action<SchemaSyncOptions> configure, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = SchemaSync.VerificationOptions(configure);
        var result = await ApplyDatabaseSchemaAsync<TContext>(serviceProvider, options, cancellationToken).ConfigureAwait(false);
        return result.HasChanges ? throw new SchemaDriftException(result) : result;
    }
}
