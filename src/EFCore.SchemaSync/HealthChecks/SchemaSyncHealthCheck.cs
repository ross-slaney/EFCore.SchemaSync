using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace EFCore.SchemaSync.HealthChecks;

/// <summary>
/// Reports whether the database matches the model of <typeparamref name="TContext"/>: Healthy when it does, the
/// configured drift status (Degraded by default) when schema changes are pending, and the registration's failure status
/// (Unhealthy by default) when the check cannot run. Uses the recorded fingerprint when possible, so a probe normally
/// costs a few catalog queries; results are cached for <see cref="SchemaSyncHealthCheckOptions.CacheDuration"/>.
/// </summary>
public sealed class SchemaSyncHealthCheck<TContext> : IHealthCheck
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SchemaSyncHealthCheckOptions _options;
    private readonly SchemaSyncHealthCheckCache _cache;

    internal SchemaSyncHealthCheck(IServiceScopeFactory scopeFactory, SchemaSyncHealthCheckOptions options, SchemaSyncHealthCheckCache cache)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGet(_options.CacheDuration, out var cached))
        {
            return cached;
        }

        await _cache.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(_options.CacheDuration, out cached))
            {
                return cached;
            }

            var result = await CheckAsync(context, cancellationToken).ConfigureAwait(false);
            _cache.Set(result);
            return result;
        }
        finally
        {
            _cache.Gate.Release();
        }
    }

    private async Task<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TContext>();
            var options = SchemaSync.VerificationOptions(_options.Configure);
            var result = await SchemaSynchronizer.ApplyAsync(dbContext, options, NullLogger.Instance, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["database"] = result.DatabaseName,
                ["context"] = result.ContextName,
                ["pendingChanges"] = result.Changes.Count,
                ["retainedObjects"] = result.RetainedObjects.Count,
                ["comparisonSkipped"] = result.ComparisonSkipped,
            };

            return result.HasChanges
                ? new HealthCheckResult(_options.DriftStatus, $"{result.Changes.Count} pending schema change(s): {string.Join("; ", result.Changes.Select(c => c.ToString()))}", data: data)
                : HealthCheckResult.Healthy($"The schema of {result.DatabaseName} matches the model of {result.ContextName}.", data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var stage = ex is SchemaSyncException schemaSync ? $" at stage {schemaSync.Stage}" : string.Empty;
            return new HealthCheckResult(context.Registration.FailureStatus, $"The schema check failed{stage}: {ex.Message}", ex);
        }
    }
}

/// <summary>Options for <see cref="SchemaSyncHealthCheck{TContext}"/>.</summary>
public sealed class SchemaSyncHealthCheckOptions
{
    /// <summary>
    /// Configures the options the application uses when it applies the schema (for example <c>ManagedSchemas</c>), so the
    /// check compares the same way and the recorded fingerprint can answer cheaply.
    /// </summary>
    public Action<SchemaSyncOptions>? Configure { get; set; }

    /// <summary>Status reported when schema changes are pending. Default: <see cref="HealthStatus.Degraded"/>.</summary>
    public HealthStatus DriftStatus { get; set; } = HealthStatus.Degraded;

    /// <summary>How long a result is reused. Default: 1 minute. <see cref="TimeSpan.Zero"/> checks on every probe.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);
}

internal sealed class SchemaSyncHealthCheckCache
{
    private (HealthCheckResult Result, DateTime At)? _last;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public bool TryGet(TimeSpan duration, out HealthCheckResult result)
    {
        if (_last is { } last && duration > TimeSpan.Zero && DateTime.UtcNow - last.At < duration)
        {
            result = last.Result;
            return true;
        }

        result = default;
        return false;
    }

    public void Set(HealthCheckResult result) => _last = (result, DateTime.UtcNow);
}
