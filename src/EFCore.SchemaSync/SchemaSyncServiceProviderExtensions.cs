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

        return await SchemaSynchronizer.ApplyAsync(context, options, logger, cancellationToken).ConfigureAwait(false);
    }
}
