using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using EFCore.SchemaSync.IntegrationTests.Infrastructure;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>End-to-end coverage of the sample application, both as an Aspire-hosted web app and in its --apply-schema mode.</summary>
[TestClass]
public sealed class SampleAppTests
{
    [TestMethod]
    public async Task The_sample_api_serves_requests_against_the_schema_it_applied_at_startup()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await SqlFixture.App.ResourceNotifications.WaitForResourceHealthyAsync("sample", cts.Token);
        using var client = SqlFixture.App.CreateHttpClient("sample");

        var name = $"groceries-{Guid.NewGuid():N}";
        using var created = await client.PostAsJsonAsync("/lists", new { name }, cts.Token);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(cts.Token));
        var list = await created.Content.ReadFromJsonAsync<ListDto>(cts.Token);
        Assert.IsNotNull(list);
        Assert.AreEqual(name, list.Name);
        Assert.IsTrue(list.CreatedAt > new DateTime(2020, 1, 1), "the SYSUTCDATETIME() default is applied");

        using var item = await client.PostAsJsonAsync($"/lists/{list.Id}/items", new { title = "Milk", dueDate = "2030-01-01" }, cts.Token);
        Assert.AreEqual(HttpStatusCode.Created, item.StatusCode, await item.Content.ReadAsStringAsync(cts.Token));
        var itemDto = await item.Content.ReadFromJsonAsync<ItemDto>(cts.Token);
        Assert.AreEqual(3, itemDto!.Priority, "the default value 3 is applied by the database");
        Assert.IsFalse(itemDto.IsDone);

        using var done = await client.PostAsync($"/lists/{list.Id}/items/{itemDto.Id}/done", null, cts.Token);
        Assert.AreEqual(HttpStatusCode.OK, done.StatusCode);

        var items = await client.GetFromJsonAsync<List<ItemDto>>($"/lists/{list.Id}/items", cts.Token);
        Assert.IsTrue(items!.Single().IsDone);

        using var missing = await client.PostAsJsonAsync("/lists/999999/items", new { title = "nope" }, cts.Token);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        var sampleDb = await SqlFixture.App.GetConnectionStringAsync("sampledb", cts.Token);
        Assert.IsNotNull(sampleDb);
        var withDefaults = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sampleDb) { TrustServerCertificate = true, Encrypt = false }.ConnectionString;
        Assert.AreEqual(1, await SqlFixture.ScalarAsync<int>(withDefaults, "SELECT COUNT(*) FROM sys.tables WHERE name = 'Lists' AND SCHEMA_NAME(schema_id) = 'app'"));
        Assert.AreEqual(1, await SqlFixture.ScalarAsync<int>(withDefaults, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Items_DueDate' AND has_filter = 1"));
    }

    [TestMethod]
    public async Task Apply_schema_flag_applies_the_schema_exits_zero_and_never_starts_the_server()
    {
        await using var db = await TestDatabase.CreateAsync();

        var first = await SampleProcess.RunAsync(db.ConnectionString, "--apply-schema");

        Assert.AreEqual(0, first.ExitCode, first.StandardError + first.StandardOutput);
        StringAssert.Contains(first.StandardOutput, "Applied");
        StringAssert.Contains(first.StandardOutput, "Create Table [app].[Lists]");
        Assert.IsTrue(await db.ObjectExistsAsync("app.Items"));
        Assert.IsFalse(first.StandardOutput.Contains("Now listening on", StringComparison.Ordinal), "Kestrel must not start");

        var second = await SampleProcess.RunAsync(db.ConnectionString, "--apply-schema");
        Assert.AreEqual(0, second.ExitCode, second.StandardError);
        StringAssert.Contains(second.StandardOutput, "Schema is current");
    }

    [TestMethod]
    public async Task Apply_schema_flag_dry_run_prints_the_script_and_changes_nothing()
    {
        await using var db = await TestDatabase.CreateAsync();

        var outcome = await SampleProcess.RunAsync(db.ConnectionString, "--apply-schema", "--dry-run");

        Assert.AreEqual(0, outcome.ExitCode, outcome.StandardError);
        StringAssert.Contains(outcome.StandardOutput, "Dry run");
        StringAssert.Contains(outcome.StandardOutput, "CREATE TABLE [app].[Items]");
        Assert.AreEqual(0, await db.TableCountAsync());
    }

    [TestMethod]
    public async Task Apply_schema_flag_fails_with_a_nonzero_exit_code_and_the_stage()
    {
        var missing = SqlFixture.ConnectionStringFor($"SchemaSync_missing_{Guid.NewGuid():N}"[..40]);

        var outcome = await SampleProcess.RunAsync(missing, "--apply-schema");

        Assert.AreEqual(1, outcome.ExitCode, outcome.StandardOutput);
        StringAssert.Contains(outcome.StandardError, "stage Connect");
    }

    private sealed record ListDto(int Id, string Name, DateTime CreatedAt);

    private sealed record ItemDto(int Id, string Title, string? Notes, int Priority, bool IsDone, DateOnly? DueDate);
}
