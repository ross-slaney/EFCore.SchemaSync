using System.Diagnostics;
using EFCore.SchemaSync.Conversion;
using EFCore.SchemaSync.Deployment;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync;

/// <summary>
/// Orchestrates one schema application: resolve the connection, convert the model, take the database lock,
/// compare, then preview or deploy. Every failure is raised as a <see cref="SchemaSyncException"/> that names
/// the stage, except cancellation which surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
internal static class SchemaSynchronizer
{
    public static async Task<SchemaSyncResult> ApplyAsync(DbContext context, SchemaSyncOptions options, ILogger logger, IServiceProvider? services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var stopwatch = Stopwatch.StartNew();
        var contextName = context.GetType().Name;
        var contextKey = context.GetType().FullName ?? contextName;
        var warnings = new List<string>();
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Resolve the target (provider check, connection string, database name).
        var target = TargetConnection.Resolve(context, options);
        logger.LogInformation(
            "EFCore.SchemaSync: applying the model of {Context} to {Server}/{Database}{Mode}.",
            contextName, target.ServerName, target.DatabaseName, options.DryRun ? " (dry run)" : string.Empty);

        // 2. Connect. This connection owns the schema lock for the rest of the operation.
        await using var connection = await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        var collation = await ReadDatabaseCollationAsync(connection, logger, cancellationToken).ConfigureAwait(false);

        // 3. Prepare the model: EF's create script and the refactor log. The DACPAC is only built when it is needed.
        var prepared = SchemaConverter.Prepare(context, new SchemaConversionOptions
        {
            TargetSqlServerVersion = options.TargetSqlServerVersion,
            Collation = collation,
            PackageName = contextName,
            RefactorLog = options.RefactorLog,
            RefactorLogPath = options.RefactorLogPath,
        });
        var trackState = options.SkipComparisonWhenUnchanged && !options.DryRun && options.ConfigureDeployOptions is null;
        var modelHash = trackState ? ModelHash.Compute(prepared, collation, options) : null;
        cancellationToken.ThrowIfCancellationRequested();

        // 4. Serialize comparison and deployment across instances.
        await using var schemaLock = await SchemaLock.AcquireAsync(connection, options.LockResourceName, options.LockTimeout, logger, cancellationToken).ConfigureAwait(false);

        // 5. Fast path: nothing changed since the last successful application.
        if (trackState)
        {
            var state = await SchemaState.TryReadAsync(connection, contextKey, logger, cancellationToken).ConfigureAwait(false);
            if (state is not null && string.Equals(state.ModelHash, modelHash, StringComparison.Ordinal))
            {
                var fingerprint = await SchemaFingerprint.TryComputeAsync(connection, logger, cancellationToken).ConfigureAwait(false);
                if (fingerprint is not null && string.Equals(state.SchemaFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "EFCore.SchemaSync: the model of {Context} and the schema of {Database} are unchanged since the last deployment; no changes needed (comparison skipped).",
                        contextName, target.DatabaseName);
                    warnings.AddRange(state.Warnings);
                    return await FinishWithoutChangesAsync(context, options, services, target, logger, contextName, state.RetainedObjects, warnings, stopwatch, cancellationToken).ConfigureAwait(false);
                }

                logger.LogDebug("EFCore.SchemaSync: the schema of {Database} changed since the last deployment; running the full comparison.", target.DatabaseName);
            }
        }

        // 6. Build the DACPAC (the library's state table is part of the model so DacFx never reports or drops it).
        var includeState = options.SkipComparisonWhenUnchanged || await SchemaState.ExistsAsync(connection, cancellationToken).ConfigureAwait(false);
        var package = prepared.Build(includeState ? [SchemaState.Statement] : null);
        warnings.AddRange(package.Warnings);
        var userObjects = package.Objects.Where(o => !SchemaState.IsInternal(o.Name)).ToList();
        logger.LogInformation(
            "EFCore.SchemaSync: converted {Context} into a DACPAC with {ObjectCount} objects ({TableCount} tables).",
            contextName, userObjects.Count, userObjects.Count(o => o.ObjectType == "Table"));
        foreach (var warning in package.Warnings)
        {
            logger.LogWarning("EFCore.SchemaSync: {Warning}", warning);
        }

        if (package.RefactorOperations.Count > 0)
        {
            logger.LogInformation(
                "EFCore.SchemaSync: {Count} refactor operation(s) are embedded (applied in place once, recorded in dbo.__RefactorLog): {Operations}",
                package.RefactorOperations.Count, string.Join("; ", package.RefactorOperations.Select(o => o.ToString())));
        }

        // 7. Compare.
        var deployer = new SchemaDeployer(target, logger);
        var deployOptions = DacDeployOptionsFactory.Create(options);
        using var dacPackage = package.LoadDacPackage();

        var report = await deployer.GenerateReportAsync(dacPackage, deployOptions, cancellationToken).ConfigureAwait(false);
        var changes = report.Operations.Where(o => !SchemaState.IsInternal(o.ObjectName)).ToList();
        var bookkeepingOnly = changes.Count == 0 && report.Operations.Count > 0;
        var dataIssues = report.DataIssues;
        if (report.DataIssueDetails.Count > 0 && !options.AllowDataLoss && !options.DryRun)
        {
            // Same rule DacFx applies when it executes with BlockOnPossibleDataLoss: a lossy operation only matters
            // when the affected table holds rows. Checking here keeps the failure deterministic and before any DDL.
            var blocking = await FindBlockingDataIssuesAsync(connection, report.DataIssueDetails, logger, cancellationToken).ConfigureAwait(false);
            if (blocking.Count > 0)
            {
                throw new SchemaChangesBlockedException(blocking, changes);
            }
        }

        foreach (var alert in report.Alerts.Where(a => !string.Equals(a.Name, "DataIssue", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.AddRange(alert.Issues.Select(issue => $"{alert.Name}: {issue}"));
        }

        var retained = await ComputeRetainedObjectsAsync(deployer, dacPackage, options, report, warnings, cancellationToken).ConfigureAwait(false);
        if (retained.Count > 0)
        {
            logger.LogWarning(
                "EFCore.SchemaSync: {Count} object(s) exist in {Database} but not in the EF model and were preserved (set AllowObjectRemoval to drop them): {Objects}",
                retained.Count, target.DatabaseName, string.Join(", ", retained.Select(r => r.ToString())));
        }

        foreach (var issue in dataIssues)
        {
            logger.LogWarning("EFCore.SchemaSync: possible data loss: {Issue}", issue);
        }

        // 8. Decide.
        if (changes.Count == 0)
        {
            logger.LogInformation("EFCore.SchemaSync: {Database} already matches the model of {Context}; no changes needed.", target.DatabaseName, contextName);
        }
        else
        {
            logger.LogInformation(
                "EFCore.SchemaSync: {Count} schema change(s) {Verb} for {Database}: {Changes}",
                changes.Count, options.DryRun ? "previewed" : "planned", target.DatabaseName,
                string.Join("; ", changes.Select(c => c.ToString())));
        }

        if (options.DryRun)
        {
            if (options.Hooks.Count > 0)
            {
                warnings.Add($"{options.Hooks.Count} hook(s) were not run because this is a dry run.");
            }

            var preview = changes.Count == 0
                ? null
                : await deployer.GenerateScriptAsync(dacPackage, deployOptions, cancellationToken).ConfigureAwait(false);
            return new SchemaSyncResult(changes.Count == 0 ? SchemaSyncOutcome.NoChangesNeeded : SchemaSyncOutcome.Previewed,
                contextName, target.ServerName, target.DatabaseName, changes, retained, dataIssues, warnings, preview, stopwatch.Elapsed);
        }

        var executedHooks = new List<string>();

        // 9. Before-deploy hooks (old schema, lock held).
        await RunHooksAsync(SchemaSyncHookPhase.BeforeDeploy, options, context, services, changes, result: null, target, logger, executedHooks, cancellationToken).ConfigureAwait(false);

        string? script = null;
        var outcome = SchemaSyncOutcome.NoChangesNeeded;
        if (report.Operations.Count > 0)
        {
            if (options.IncludeDeploymentScript && changes.Count > 0)
            {
                script = await deployer.GenerateScriptAsync(dacPackage, deployOptions, cancellationToken).ConfigureAwait(false);
            }

            // 10. Deploy (a bookkeeping-only deployment just creates the state table; it is not reported as a change).
            await deployer.DeployAsync(dacPackage, deployOptions, cancellationToken).ConfigureAwait(false);
            if (!bookkeepingOnly)
            {
                outcome = SchemaSyncOutcome.Applied;
                logger.LogInformation(
                    "EFCore.SchemaSync: applied {Count} schema change(s) to {Server}/{Database} in {Elapsed:0.0}s.",
                    changes.Count, target.ServerName, target.DatabaseName, stopwatch.Elapsed.TotalSeconds);
            }
        }

        // 11. Record the state for the next start's fast path (before the after-deploy hooks: DDL a hook performs is
        //     then detected as a change next time instead of being taken as the expected schema).
        if (trackState)
        {
            var fingerprint = await SchemaFingerprint.TryComputeAsync(connection, logger, cancellationToken).ConfigureAwait(false);
            if (fingerprint is not null)
            {
                await SchemaState.TryWriteAsync(connection, contextKey, new SchemaState.Entry(modelHash!, fingerprint, retained, package.Warnings), logger, cancellationToken).ConfigureAwait(false);
            }
        }

        // 12. After-deploy hooks (schema matches the model, lock held).
        var applied = new SchemaSyncResult(outcome, contextName, target.ServerName, target.DatabaseName,
            changes, retained, dataIssues, warnings, script, stopwatch.Elapsed, executedHooks.ToArray());
        await RunHooksAsync(SchemaSyncHookPhase.AfterDeploy, options, context, services, changes, applied, target, logger, executedHooks, cancellationToken).ConfigureAwait(false);

        return new SchemaSyncResult(outcome, contextName, target.ServerName, target.DatabaseName,
            changes, retained, dataIssues, warnings, script, stopwatch.Elapsed, executedHooks);
    }

    /// <summary>The fast path's end: no DacFx, but hooks still run (they run on every real application).</summary>
    private static async Task<SchemaSyncResult> FinishWithoutChangesAsync(
        DbContext context,
        SchemaSyncOptions options,
        IServiceProvider? services,
        TargetConnection target,
        ILogger logger,
        string contextName,
        IReadOnlyList<SchemaObjectReference> retained,
        List<string> warnings,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var executedHooks = new List<string>();
        await RunHooksAsync(SchemaSyncHookPhase.BeforeDeploy, options, context, services, [], result: null, target, logger, executedHooks, cancellationToken).ConfigureAwait(false);
        var current = new SchemaSyncResult(SchemaSyncOutcome.NoChangesNeeded, contextName, target.ServerName, target.DatabaseName,
            [], retained, [], warnings, deploymentScript: null, stopwatch.Elapsed, executedHooks.ToArray(), comparisonSkipped: true);
        await RunHooksAsync(SchemaSyncHookPhase.AfterDeploy, options, context, services, [], current, target, logger, executedHooks, cancellationToken).ConfigureAwait(false);
        return new SchemaSyncResult(SchemaSyncOutcome.NoChangesNeeded, contextName, target.ServerName, target.DatabaseName,
            [], retained, [], warnings, deploymentScript: null, stopwatch.Elapsed, executedHooks, comparisonSkipped: true);
    }

    private static async Task RunHooksAsync(
        SchemaSyncHookPhase phase,
        SchemaSyncOptions options,
        DbContext context,
        IServiceProvider? services,
        IReadOnlyList<SchemaChange> changes,
        SchemaSyncResult? result,
        TargetConnection target,
        ILogger logger,
        List<string> executed,
        CancellationToken cancellationToken)
    {
        var hooks = options.Hooks.Where(h => h.Phase == phase).ToList();
        if (hooks.Count == 0)
        {
            return;
        }

        var hookContext = new SchemaSyncHookContext(phase, context, services, changes, result, target.ServerName, target.DatabaseName, logger);
        foreach (var hook in hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.StartNew();
            logger.LogInformation("EFCore.SchemaSync: running {Phase} hook '{Hook}'.", phase, hook.Name);
            try
            {
                await hook.Callback(hookContext, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var (stage, consequence) = phase == SchemaSyncHookPhase.BeforeDeploy
                    ? (SchemaSyncStage.BeforeDeployHook, "No schema change was executed.")
                    : (SchemaSyncStage.AfterDeployHook, "The schema changes were already applied; later hooks did not run.");
                throw new SchemaSyncException(stage, $"{phase} hook '{hook.Name}' failed: {ex.Message} {consequence}", ex);
            }

            executed.Add(hook.Name);
            logger.LogInformation("EFCore.SchemaSync: {Phase} hook '{Hook}' completed in {Elapsed:0.0}s.", phase, hook.Name, started.Elapsed.TotalSeconds);
        }
    }

    private static async Task<IReadOnlyList<SchemaObjectReference>> ComputeRetainedObjectsAsync(
        SchemaDeployer deployer, Microsoft.SqlServer.Dac.DacPackage dacPackage, SchemaSyncOptions options, DeployReport effectiveReport, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.ReportRetainedObjects || options.AllowObjectRemoval)
        {
            return [];
        }

        try
        {
            var dropEverything = DacDeployOptionsFactory.CreateForRetainedObjectsReport(options);
            var dropReport = await deployer.GenerateReportAsync(dacPackage, dropEverything, cancellationToken).ConfigureAwait(false);
            var plannedDrops = effectiveReport.Operations
                .Where(c => c.Kind == SchemaChangeKind.Drop)
                .Select(c => (c.ObjectType, c.ObjectName))
                .ToHashSet();

            return dropReport.Operations
                .Where(c => c.Kind == SchemaChangeKind.Drop && !plannedDrops.Contains((c.ObjectType, c.ObjectName)) && !SchemaState.IsInternal(c.ObjectName))
                .Select(c => new SchemaObjectReference(c.ObjectType, c.ObjectName))
                .Distinct()
                .ToArray();
        }
        catch (SchemaSyncException ex)
        {
            warnings.Add("Could not determine which target-only objects were retained: " + ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Returns the data issues whose affected tables contain rows (or that cannot be attributed to a table and are
    /// therefore treated as blocking).
    /// </summary>
    private static async Task<IReadOnlyList<string>> FindBlockingDataIssuesAsync(SqlConnection connection, IReadOnlyList<DataIssue> issues, ILogger logger, CancellationToken cancellationToken)
    {
        var blocking = new List<string>();
        var rowCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var issue in issues)
        {
            var tables = issue.AffectedOperations
                .Where(o => string.Equals(o.ObjectType, "Table", StringComparison.Ordinal))
                .Select(o => o.ObjectName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (tables.Count == 0)
            {
                blocking.Add(issue.Message);
                continue;
            }

            var hasRows = false;
            foreach (var table in tables)
            {
                if (!rowCache.TryGetValue(table, out var tableHasRows))
                {
                    tableHasRows = await TableHasRowsAsync(connection, table, cancellationToken).ConfigureAwait(false);
                    rowCache[table] = tableHasRows;
                }

                hasRows |= tableHasRows;
            }

            if (hasRows)
            {
                blocking.Add(issue.Message);
            }
            else
            {
                logger.LogWarning("EFCore.SchemaSync: DacFx flagged a potentially lossy change but the affected table is empty; proceeding: {Issue}", issue.Message);
            }
        }

        return blocking;
    }

    private static async Task<bool> TableHasRowsAsync(SqlConnection connection, string quotedTableName, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {quotedTableName}) THEN 1 ELSE 0 END";
            command.CommandTimeout = 60;
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is int rows && rows == 1;
        }
        catch (SqlException)
        {
            return true; // cannot prove the table is empty: stay on the safe side
        }
    }

    private static async Task<string?> ReadDatabaseCollationAsync(SqlConnection connection, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))";
            command.CommandTimeout = 30;
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value as string;
        }
        catch (SqlException ex)
        {
            logger.LogDebug(ex, "EFCore.SchemaSync: could not read the database collation; using the DacFx default.");
            return null;
        }
    }
}
