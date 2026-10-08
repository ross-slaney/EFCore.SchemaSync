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
    public static async Task<SchemaSyncResult> ApplyAsync(DbContext context, SchemaSyncOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var stopwatch = Stopwatch.StartNew();
        var contextName = context.GetType().Name;
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

        // 3. Convert the EF model into a validated DACPAC (no database access).
        var package = SchemaConverter.Convert(context, new SchemaConversionOptions
        {
            TargetSqlServerVersion = options.TargetSqlServerVersion,
            Collation = collation,
            PackageName = contextName,
        });
        warnings.AddRange(package.Warnings);
        logger.LogInformation(
            "EFCore.SchemaSync: converted {Context} into a DACPAC with {ObjectCount} objects ({TableCount} tables).",
            contextName, package.Objects.Count, package.Objects.Count(o => o.ObjectType == "Table"));
        foreach (var warning in package.Warnings)
        {
            logger.LogWarning("EFCore.SchemaSync: {Warning}", warning);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4. Serialize comparison and deployment across instances.
        await using var schemaLock = await SchemaLock.AcquireAsync(connection, options.LockResourceName, options.LockTimeout, logger, cancellationToken).ConfigureAwait(false);

        // 5. Compare.
        var deployer = new SchemaDeployer(target, logger);
        var deployOptions = DacDeployOptionsFactory.Create(options);
        using var dacPackage = package.LoadDacPackage();

        var report = await deployer.GenerateReportAsync(dacPackage, deployOptions, cancellationToken).ConfigureAwait(false);
        var dataIssues = report.DataIssues;
        if (report.DataIssueDetails.Count > 0 && !options.AllowDataLoss && !options.DryRun)
        {
            // Same rule DacFx applies when it executes with BlockOnPossibleDataLoss: a lossy operation only matters
            // when the affected table holds rows. Checking here keeps the failure deterministic and before any DDL.
            var blocking = await FindBlockingDataIssuesAsync(connection, report.DataIssueDetails, logger, cancellationToken).ConfigureAwait(false);
            if (blocking.Count > 0)
            {
                throw new SchemaChangesBlockedException(blocking, report.Operations);
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

        // 6. Decide.
        if (report.Operations.Count == 0)
        {
            logger.LogInformation("EFCore.SchemaSync: {Database} already matches the model of {Context}; no changes needed.", target.DatabaseName, contextName);
            return new SchemaSyncResult(SchemaSyncOutcome.NoChangesNeeded, contextName, target.ServerName, target.DatabaseName,
                report.Operations, retained, dataIssues, warnings, deploymentScript: null, stopwatch.Elapsed);
        }

        logger.LogInformation(
            "EFCore.SchemaSync: {Count} schema change(s) {Verb} for {Database}: {Changes}",
            report.Operations.Count, options.DryRun ? "previewed" : "planned", target.DatabaseName,
            string.Join("; ", report.Operations.Select(c => c.ToString())));

        string? script = null;
        if (options.DryRun || options.IncludeDeploymentScript)
        {
            script = await deployer.GenerateScriptAsync(dacPackage, deployOptions, cancellationToken).ConfigureAwait(false);
        }

        if (options.DryRun)
        {
            return new SchemaSyncResult(SchemaSyncOutcome.Previewed, contextName, target.ServerName, target.DatabaseName,
                report.Operations, retained, dataIssues, warnings, script, stopwatch.Elapsed);
        }

        // 7. Deploy.
        await deployer.DeployAsync(dacPackage, deployOptions, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "EFCore.SchemaSync: applied {Count} schema change(s) to {Server}/{Database} in {Elapsed:0.0}s.",
            report.Operations.Count, target.ServerName, target.DatabaseName, stopwatch.Elapsed.TotalSeconds);

        return new SchemaSyncResult(SchemaSyncOutcome.Applied, contextName, target.ServerName, target.DatabaseName,
            report.Operations, retained, dataIssues, warnings, script, stopwatch.Elapsed);
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
                .Where(c => c.Kind == SchemaChangeKind.Drop && !plannedDrops.Contains((c.ObjectType, c.ObjectName)))
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
