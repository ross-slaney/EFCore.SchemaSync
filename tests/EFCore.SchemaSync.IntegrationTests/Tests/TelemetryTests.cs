using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>
/// The library must never make an application send DacFx usage data. DacFx 170 only starts its telemetry session
/// after <c>DacServices.EnableTelemetry</c>; these tests fail if a DacFx upgrade changes that default or if the
/// library ever enables it.
/// </summary>
[TestClass]
public sealed class TelemetryTests
{
    [TestMethod]
    public async Task Deployments_do_not_enable_DacFx_telemetry_or_contact_Application_Insights()
    {
        var requests = new ConcurrentQueue<string>();
        using var subscription = ApplicationInsightsRequestObserver.Subscribe(requests);
        await using var db = await TestDatabase.CreateAsync();

        await db.ApplyShapeAsync(SchemaShape.V1);
        await db.ApplyShapeAsync(SchemaShape.V1 with { Phone = true });

        Assert.AreNotEqual(true, DacFxTelemetry.SessionEnabled(), "DacFx telemetry must stay disabled unless the application enables it");

        // Application Insights' in-memory channel flushes every 30 seconds: wait past one flush.
        await Task.Delay(TimeSpan.FromSeconds(35));
        Assert.AreEqual(0, requests.Count, "no request to Application Insights: " + string.Join(", ", requests));
    }

    private static class DacFxTelemetry
    {
        /// <summary>Reads DacFx's process-wide telemetry switch (null = never configured, which DacFx treats as disabled).</summary>
        public static bool? SessionEnabled()
        {
            var type = typeof(Microsoft.SqlServer.Dac.DacServices).Assembly.GetReferencedAssemblies()
                .Select(name => { try { return Assembly.Load(name); } catch (FileNotFoundException) { return null; } })
                .Concat(AppDomain.CurrentDomain.GetAssemblies())
                .Where(assembly => assembly is not null)
                .Select(assembly => assembly!.GetType("Microsoft.Data.Tools.Schema.Common.Telemetry.TelemetrySession", throwOnError: false))
                .FirstOrDefault(t => t is not null);
            Assert.IsNotNull(type, "DacFx's telemetry internals changed (TelemetrySession not found). Re-verify that DacFx telemetry is off by default and update this test.");

            var field = type.GetField("_enabled", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "DacFx's telemetry internals changed (TelemetrySession._enabled not found). Re-verify that DacFx telemetry is off by default and update this test.");
            return (bool?)field.GetValue(null);
        }
    }

    private sealed class ApplicationInsightsRequestObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly ConcurrentQueue<string> _requests;
        private readonly List<IDisposable> _subscriptions = [];

        private ApplicationInsightsRequestObserver(ConcurrentQueue<string> requests) => _requests = requests;

        public static IDisposable Subscribe(ConcurrentQueue<string> requests)
        {
            var observer = new ApplicationInsightsRequestObserver(requests);
            observer._subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(observer));
            return new Unsubscriber(observer._subscriptions);
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "HttpHandlerDiagnosticListener")
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) is HttpRequestMessage { RequestUri: { } uri }
                && (uri.Host.Contains("applicationinsights", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("services.visualstudio.com", StringComparison.OrdinalIgnoreCase)))
            {
                _requests.Enqueue(uri.ToString());
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }

        private sealed class Unsubscriber(List<IDisposable> subscriptions) : IDisposable
        {
            public void Dispose()
            {
                lock (subscriptions)
                {
                    foreach (var subscription in subscriptions)
                    {
                        subscription.Dispose();
                    }
                }
            }
        }
    }
}
