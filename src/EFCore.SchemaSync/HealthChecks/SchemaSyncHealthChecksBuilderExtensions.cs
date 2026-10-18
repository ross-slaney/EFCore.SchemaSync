using EFCore.SchemaSync.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="SchemaSyncHealthCheck{TContext}"/>.</summary>
public static class SchemaSyncHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a health check that reports whether the database matches the model of <typeparamref name="TContext"/>.
    /// Pass the same configuration the application uses when it applies the schema.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">Health check name. Default: <c>efcore-schemasync</c>.</param>
    /// <param name="configure">Health check options (drift status, cache duration, schema options).</param>
    /// <param name="failureStatus">Status when the check cannot run. Default: <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Tags for filtering health checks.</param>
    public static IHealthChecksBuilder AddSchemaSyncHealthCheck<TContext>(
        this IHealthChecksBuilder builder,
        string name = "efcore-schemasync",
        Action<SchemaSyncHealthCheckOptions>? configure = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new SchemaSyncHealthCheckOptions();
        configure?.Invoke(options);
        var cache = new SchemaSyncHealthCheckCache();
        return builder.Add(new HealthCheckRegistration(
            name,
            services => new SchemaSyncHealthCheck<TContext>(services.GetRequiredService<IServiceScopeFactory>(), options, cache),
            failureStatus ?? HealthStatus.Unhealthy,
            tags));
    }
}
