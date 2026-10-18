using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using EFCore.SchemaSync.Deployment;

namespace EFCore.SchemaSync;

/// <summary>
/// OpenTelemetry-compatible tracing and metrics. Subscribe with
/// <c>.WithTracing(t =&gt; t.AddSource(SchemaSyncTelemetry.SourceName)).WithMetrics(m =&gt; m.AddMeter(SchemaSyncTelemetry.MeterName))</c>.
/// Without a listener nothing is recorded and the instrumentation costs nothing measurable.
/// </summary>
public static class SchemaSyncTelemetry
{
    /// <summary>Name of the <see cref="ActivitySource"/>: one <c>EFCore.SchemaSync.Apply</c> activity per application, with a child per stage.</summary>
    public const string SourceName = "EFCore.SchemaSync";

    /// <summary>Name of the <see cref="Meter"/>.</summary>
    public const string MeterName = "EFCore.SchemaSync";

    /// <summary>Histogram (seconds) of whole applications, tagged with the outcome.</summary>
    public const string ApplyDurationInstrument = "efcore.schemasync.apply.duration";

    /// <summary>Histogram (seconds) of each stage, tagged with the stage and its status.</summary>
    public const string StageDurationInstrument = "efcore.schemasync.stage.duration";

    /// <summary>Histogram (seconds) of the time spent waiting for the schema lock.</summary>
    public const string LockWaitInstrument = "efcore.schemasync.lock.wait";

    /// <summary>Counter of applications, tagged with the outcome (or <c>failed</c> plus <c>error.type</c>).</summary>
    public const string ApplicationsInstrument = "efcore.schemasync.applications";

    /// <summary>Counter of planned or applied schema changes, tagged with the change kind.</summary>
    public const string ChangesInstrument = "efcore.schemasync.changes";

    internal const string ContextTag = "efcore.schemasync.context";
    internal const string OutcomeTag = "efcore.schemasync.outcome";
    internal const string StageTag = "efcore.schemasync.stage";
    internal const string DryRunTag = "efcore.schemasync.dry_run";
    internal const string ChangeKindTag = "efcore.schemasync.change.kind";
    internal const string StatusTag = "efcore.schemasync.status";
    internal const string HookTag = "efcore.schemasync.hook";
    internal const string HookPhaseTag = "efcore.schemasync.hook.phase";
    internal const string DatabaseTag = "db.namespace";
    internal const string ServerTag = "server.address";
    internal const string SystemTag = "db.system.name";
    internal const string ErrorTypeTag = "error.type";

    private static readonly string Version = typeof(SchemaSyncTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    internal static readonly ActivitySource Source = new(SourceName, Version);

    internal static readonly Meter Meter = new(MeterName, Version);

    internal static readonly Histogram<double> ApplyDuration = Meter.CreateHistogram<double>(ApplyDurationInstrument, "s", "Duration of a schema application, including the lock wait.");

    internal static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>(StageDurationInstrument, "s", "Duration of one stage of a schema application.");

    internal static readonly Histogram<double> LockWait = Meter.CreateHistogram<double>(LockWaitInstrument, "s", "Time spent waiting for the database-scoped schema lock.");

    internal static readonly Counter<long> Applications = Meter.CreateCounter<long>(ApplicationsInstrument, "{application}", "Schema applications by outcome.");

    internal static readonly Counter<long> Changes = Meter.CreateCounter<long>(ChangesInstrument, "{change}", "Schema changes planned (dry run) or applied, by kind.");
}

/// <summary>Telemetry for one schema application: the root activity plus metrics.</summary>
internal sealed class SchemaSyncRun : IDisposable
{
    private readonly Activity? _activity;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly string _context;
    private readonly bool _dryRun;
    private string? _database;
    private string _outcome = "failed";
    private string? _errorType;

    private SchemaSyncRun(string context, bool dryRun)
    {
        _context = context;
        _dryRun = dryRun;
        _activity = SchemaSyncTelemetry.Source.StartActivity("EFCore.SchemaSync.Apply", ActivityKind.Internal);
        _activity?.SetTag(SchemaSyncTelemetry.ContextTag, context);
        _activity?.SetTag(SchemaSyncTelemetry.DryRunTag, dryRun);
        _activity?.SetTag(SchemaSyncTelemetry.SystemTag, "microsoft.sql_server");
    }

    public static SchemaSyncRun Start(string context, bool dryRun) => new(context, dryRun);

    public void SetTarget(TargetConnection target)
    {
        _database = target.DatabaseName;
        _activity?.SetTag(SchemaSyncTelemetry.DatabaseTag, target.DatabaseName);
        _activity?.SetTag(SchemaSyncTelemetry.ServerTag, target.ServerName);
    }

    public Stage StartStage(string name, params (string Key, object? Value)[] tags) => new(this, name, tags);

    public void Complete(SchemaSyncResult result)
    {
        _outcome = result.Outcome.ToString();
        if (_activity is not null)
        {
            _activity.SetTag(SchemaSyncTelemetry.OutcomeTag, _outcome);
            _activity.SetTag("efcore.schemasync.changes", result.Changes.Count);
            _activity.SetTag("efcore.schemasync.retained_objects", result.RetainedObjects.Count);
            _activity.SetTag("efcore.schemasync.data_loss_risks", result.DataLossRisks.Count);
            _activity.SetTag("efcore.schemasync.comparison_skipped", result.ComparisonSkipped);
            _activity.SetStatus(ActivityStatusCode.Ok);
        }

        foreach (var group in result.Changes.GroupBy(c => c.Kind))
        {
            SchemaSyncTelemetry.Changes.Add(group.Count(), Tags((SchemaSyncTelemetry.ChangeKindTag, group.Key.ToString()), (SchemaSyncTelemetry.DryRunTag, _dryRun)));
        }
    }

    public void Fail(Exception exception)
    {
        _errorType = exception.GetType().FullName ?? exception.GetType().Name;
        _outcome = exception is OperationCanceledException ? "cancelled" : "failed";
        if (_activity is not null)
        {
            _activity.SetTag(SchemaSyncTelemetry.OutcomeTag, _outcome);
            _activity.SetTag(SchemaSyncTelemetry.ErrorTypeTag, _errorType);
            if (exception is SchemaSyncException schemaSync)
            {
                _activity.SetTag(SchemaSyncTelemetry.StageTag, schemaSync.Stage.ToString());
            }

            _activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            _activity.AddException(exception);
        }
    }

    public void Dispose()
    {
        var tags = Tags((SchemaSyncTelemetry.OutcomeTag, _outcome), (SchemaSyncTelemetry.DryRunTag, _dryRun), (SchemaSyncTelemetry.ErrorTypeTag, _errorType));
        SchemaSyncTelemetry.ApplyDuration.Record(_stopwatch.Elapsed.TotalSeconds, tags);
        SchemaSyncTelemetry.Applications.Add(1, tags);
        _activity?.Dispose();
    }

    internal TagList Tags(params (string Key, object? Value)[] extra)
    {
        var tags = new TagList { { SchemaSyncTelemetry.ContextTag, _context } };
        if (_database is not null)
        {
            tags.Add(SchemaSyncTelemetry.DatabaseTag, _database);
        }

        foreach (var (key, value) in extra)
        {
            if (value is not null)
            {
                tags.Add(key, value);
            }
        }

        return tags;
    }

    /// <summary>A child activity for one stage. Call <see cref="Succeed"/> at the end; disposing without it records an error.</summary>
    internal sealed class Stage : IDisposable
    {
        private readonly SchemaSyncRun _run;
        private readonly string _name;
        private readonly Activity? _activity;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _succeeded;
        private bool _disposed;

        public Stage(SchemaSyncRun run, string name, (string Key, object? Value)[] tags)
        {
            _run = run;
            _name = name;
            _activity = SchemaSyncTelemetry.Source.StartActivity("EFCore.SchemaSync." + name, ActivityKind.Internal);
            foreach (var (key, value) in tags)
            {
                _activity?.SetTag(key, value);
            }
        }

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void Succeed()
        {
            _succeeded = true;
            _activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_succeeded)
            {
                _activity?.SetStatus(ActivityStatusCode.Error);
            }

            SchemaSyncTelemetry.StageDuration.Record(_stopwatch.Elapsed.TotalSeconds, _run.Tags((SchemaSyncTelemetry.StageTag, _name), (SchemaSyncTelemetry.StatusTag, _succeeded ? "ok" : "error")));
            _activity?.Dispose();
        }
    }
}
