using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// Database-scoped exclusive application lock (<c>sp_getapplock</c>, session owner) that serializes schema
/// comparison and deployment across independent application instances. The lock lives as long as the
/// session that took it, so closing the connection always releases it even if the process dies mid-deployment.
/// </summary>
internal sealed class SchemaLock : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly ILogger _logger;
    private bool _released;

    private SchemaLock(SqlConnection connection, string resourceName, ILogger logger)
    {
        _connection = connection;
        ResourceName = resourceName;
        _logger = logger;
    }

    public string ResourceName { get; }

    /// <summary>Acquires the lock on an open connection, waiting at most <paramref name="timeout"/>.</summary>
    public static async Task<SchemaLock> AcquireAsync(SqlConnection connection, string resourceName, TimeSpan timeout, ILogger logger, CancellationToken cancellationToken)
    {
        var timeoutMilliseconds = checked((int)Math.Ceiling(timeout.TotalMilliseconds));

        await using var command = connection.CreateCommand();
        command.CommandText = "sys.sp_getapplock";
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = checked((int)Math.Ceiling(timeout.TotalSeconds) + 30);
        command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = resourceName });
        command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
        command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Session" });
        command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = timeoutMilliseconds });
        var returnValue = new SqlParameter("@ReturnValue", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        command.Parameters.Add(returnValue);

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Schema lock acquisition was cancelled.", ex, cancellationToken);
        }
        catch (SqlException ex)
        {
            throw new SchemaSyncException(SchemaSyncStage.AcquireLock, $"sp_getapplock failed: {ex.Message}", ex);
        }

        var code = returnValue.Value is int value ? value : -999;
        switch (code)
        {
            case >= 0:
                logger.LogDebug("Acquired schema lock '{LockResource}' after {ElapsedMilliseconds} ms.", resourceName, started.ElapsedMilliseconds);
                return new SchemaLock(connection, resourceName, logger);
            case -1:
                throw new SchemaLockTimeoutException(resourceName, timeout);
            case -2:
                cancellationToken.ThrowIfCancellationRequested();
                throw new SchemaSyncException(SchemaSyncStage.AcquireLock, $"The schema lock request for '{resourceName}' was cancelled by SQL Server.");
            case -3:
                throw new SchemaSyncException(SchemaSyncStage.AcquireLock, $"The schema lock request for '{resourceName}' was chosen as a deadlock victim.");
            default:
                throw new SchemaSyncException(SchemaSyncStage.AcquireLock, $"sp_getapplock returned {code} for '{resourceName}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (_connection.State != ConnectionState.Open)
        {
            return; // a closed session has already released every session-owned lock
        }

        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "sys.sp_releaseapplock";
            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = 30;
            command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = ResourceName });
            command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Session" });
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Released schema lock '{LockResource}'.", ResourceName);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Closing the connection releases the lock anyway; this is only the polite path.
            _logger.LogDebug(ex, "sp_releaseapplock for '{LockResource}' failed; the lock is released when the connection closes.", ResourceName);
        }
    }
}
