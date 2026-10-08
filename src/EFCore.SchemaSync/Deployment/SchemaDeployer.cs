using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// Thin wrapper around <see cref="DacServices"/>: compares a DACPAC with the live database (deploy report),
/// scripts the deployment, or executes it. DacFx is synchronous, so each operation runs on the thread pool
/// and honors the cancellation token DacFx receives.
/// </summary>
internal sealed class SchemaDeployer
{
    private readonly TargetConnection _target;
    private readonly ILogger _logger;

    public SchemaDeployer(TargetConnection target, ILogger logger)
    {
        _target = target;
        _logger = logger;
    }

    public Task<DeployReport> GenerateReportAsync(DacPackage package, DacDeployOptions options, CancellationToken cancellationToken)
        => RunAsync(SchemaSyncStage.Compare, cancellationToken, services =>
        {
            var xml = services.GenerateDeployReport(package, _target.DatabaseName, options, cancellationToken);
            return DeployReport.Parse(xml);
        });

    public Task<string> GenerateScriptAsync(DacPackage package, DacDeployOptions options, CancellationToken cancellationToken)
        => RunAsync(SchemaSyncStage.Compare, cancellationToken, services => services.GenerateDeployScript(package, _target.DatabaseName, options, cancellationToken));

    public Task DeployAsync(DacPackage package, DacDeployOptions options, CancellationToken cancellationToken)
        => RunAsync(SchemaSyncStage.Deploy, cancellationToken, services =>
        {
            services.Deploy(package, _target.DatabaseName, upgradeExisting: true, options, cancellationToken);
            return true;
        });

    private async Task<T> RunAsync<T>(SchemaSyncStage stage, CancellationToken cancellationToken, Func<DacServices, T> action)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(() =>
            {
                var services = CreateServices();
                return action(services);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException($"The {stage} stage was cancelled.", ex, cancellationToken);
        }
        catch (DacServicesException ex)
        {
            throw new SchemaSyncException(stage, Describe(stage, ex), ex);
        }
        catch (SqlException ex)
        {
            throw new SchemaSyncException(stage, $"SQL Server returned an error during {stage}: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not SchemaSyncException)
        {
            throw new SchemaSyncException(stage, $"Unexpected failure during {stage}: {ex.Message}", ex);
        }
    }

    private DacServices CreateServices()
    {
        var services = _target.AuthProvider is { } authProvider
            ? new DacServices(_target.ConnectionString, authProvider)
            : new DacServices(_target.ConnectionString);
        services.Message += OnMessage;
        return services;
    }

    private void OnMessage(object? sender, DacMessageEventArgs e)
    {
        var message = e.Message;
        switch (message.MessageType)
        {
            case DacMessageType.Error:
                _logger.LogError("DacFx {Prefix}{Number}: {Message}", message.Prefix, message.Number, message.Message);
                break;
            case DacMessageType.Warning:
                _logger.LogWarning("DacFx {Prefix}{Number}: {Message}", message.Prefix, message.Number, message.Message);
                break;
            default:
                _logger.LogDebug("DacFx: {Message}", message.Message);
                break;
        }
    }

    private static string Describe(SchemaSyncStage stage, DacServicesException ex)
    {
        var details = ex.Messages?
            .Where(m => m.MessageType != DacMessageType.Message)
            .Select(m => $"{m.Prefix}{m.Number}: {m.Message}".Trim())
            .Distinct()
            .ToList() ?? [];

        var headline = stage == SchemaSyncStage.Deploy ? "DacFx deployment failed" : "DacFx comparison failed";
        var text = $"{headline}: {ex.Message.Trim()}";
        if (ex.InnerException is { } inner && !text.Contains(inner.Message, StringComparison.Ordinal))
        {
            text += $" Caused by {inner.GetType().Name}: {inner.Message.Trim()}";
        }

        if (details.Count > 0)
        {
            text += Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", details);
        }

        return text;
    }
}
