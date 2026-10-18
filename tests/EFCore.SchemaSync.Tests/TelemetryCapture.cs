using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EFCore.SchemaSync.Tests;

/// <summary>Captures the library's activities and measurements. Listeners are process-wide, so filter by tags.</summary>
public sealed class TelemetryCapture : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();

    public TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SchemaSyncTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Activities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_activities);

        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SchemaSyncTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        _meters.Start();
    }

    public ConcurrentQueue<Activity> Activities { get; } = new();

    public ConcurrentQueue<Measurement> Measurements { get; } = new();

    /// <summary>The root activities whose tag <paramref name="key"/> equals <paramref name="value"/>.</summary>
    public List<Activity> Roots(string key, object value)
        => Activities.Where(a => a.OperationName == "EFCore.SchemaSync.Apply" && Equals(a.GetTagItem(key), value)).OrderBy(a => a.StartTimeUtc).ToList();

    /// <summary>The direct children of <paramref name="root"/>, in start order.</summary>
    public List<Activity> Children(Activity root)
        => Activities.Where(a => a.ParentSpanId == root.SpanId && a.TraceId == root.TraceId).OrderBy(a => a.StartTimeUtc).ToList();

    public List<Measurement> For(string instrument, string key, object value)
        => Measurements.Where(m => m.Instrument == instrument && m.Tags.TryGetValue(key, out var v) && Equals(v, value)).ToList();

    public void Dispose()
    {
        _activities.Dispose();
        _meters.Dispose();
    }

    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
}
