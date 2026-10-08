using EFCore.SchemaSync.Tests.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.SchemaSync.Tests;

/// <summary>Behavior of the public entry points that can be verified without a SQL Server.</summary>
[TestClass]
public sealed class PublicApiTests
{
    [TestMethod]
    public async Task Unregistered_context_fails_at_the_resolve_stage()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => services.ApplyDatabaseSchemaAsync<MatrixDbContext>());

        Assert.AreEqual(SchemaSyncStage.ResolveContext, ex.Stage);
        StringAssert.Contains(ex.Message, nameof(MatrixDbContext));
        Assert.IsInstanceOfType<InvalidOperationException>(ex.InnerException);
    }

    [TestMethod]
    public async Task Non_sql_server_context_fails_at_the_resolve_stage()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DefaultSchemaDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        await using var provider = services.BuildServiceProvider();

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => provider.ApplyDatabaseSchemaAsync<DefaultSchemaDbContext>());

        Assert.AreEqual(SchemaSyncStage.ResolveContext, ex.Stage);
        StringAssert.Contains(ex.Message, "Sqlite");
    }

    [TestMethod]
    public async Task Connection_string_without_a_database_fails_at_the_resolve_stage()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DefaultSchemaDbContext>(o => o.UseSqlServer("Server=(local);Integrated Security=true"));
        await using var provider = services.BuildServiceProvider();

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => provider.ApplyDatabaseSchemaAsync<DefaultSchemaDbContext>());

        Assert.AreEqual(SchemaSyncStage.ResolveContext, ex.Stage);
        StringAssert.Contains(ex.Message, "Initial Catalog");
    }

    [TestMethod]
    public async Task Already_cancelled_token_cancels_before_any_work()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DefaultSchemaDbContext>(o => o.UseSqlServer(TestContextFactory.OfflineConnectionString));
        await using var provider = services.BuildServiceProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ApplyDatabaseSchemaAsync<DefaultSchemaDbContext>(cts.Token));
    }

    [TestMethod]
    public async Task Invalid_options_are_rejected_before_connecting()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DefaultSchemaDbContext>(o => o.UseSqlServer(TestContextFactory.OfflineConnectionString));
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => provider.ApplyDatabaseSchemaAsync<DefaultSchemaDbContext>(o => o.LockTimeout = TimeSpan.Zero));
    }

    [TestMethod]
    public async Task Unreachable_server_fails_at_the_connect_stage_with_the_sql_exception_as_cause()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DefaultSchemaDbContext>(o => o.UseSqlServer("Server=127.0.0.1,1;Database=Nope;User Id=sa;Password=nope;TrustServerCertificate=true;Connect Timeout=2"));
        await using var provider = services.BuildServiceProvider();

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => provider.ApplyDatabaseSchemaAsync<DefaultSchemaDbContext>());

        Assert.AreEqual(SchemaSyncStage.Connect, ex.Stage);
        Assert.IsNotNull(ex.InnerException);
        Assert.IsFalse(ex.Message.Contains("Password=nope", StringComparison.OrdinalIgnoreCase), "credentials must never appear in messages");
    }

    [TestMethod]
    public void Exceptions_name_their_stage()
    {
        var ex = new SchemaSyncException(SchemaSyncStage.Deploy, "boom", new InvalidOperationException("inner"));

        StringAssert.Contains(ex.Message, "boom");
        StringAssert.Contains(ex.Message, "Deploy");
        Assert.AreEqual("inner", ex.InnerException!.Message);

        var blocked = new SchemaChangesBlockedException(["col X dropped"], [new SchemaChange(SchemaChangeKind.Alter, "Table", "[dbo].[T]", "Alter")]);
        Assert.AreEqual(SchemaSyncStage.Compare, blocked.Stage);
        StringAssert.Contains(blocked.Message, "AllowDataLoss");
        StringAssert.Contains(blocked.Message, "col X dropped");

        var timeout = new SchemaLockTimeoutException("EFCore.SchemaSync", TimeSpan.FromSeconds(5));
        Assert.AreEqual(SchemaSyncStage.AcquireLock, timeout.Stage);
        StringAssert.Contains(timeout.Message, "LockTimeout");
    }

    [TestMethod]
    public void Result_describes_itself()
    {
        var result = new SchemaSyncResult(
            SchemaSyncOutcome.Applied,
            "AppDbContext",
            "localhost",
            "AppDb",
            [new SchemaChange(SchemaChangeKind.Create, "Table", "[dbo].[T]", "Create")],
            [new SchemaObjectReference("Table", "[dbo].[Legacy]")],
            ["col dropped"],
            ["seed skipped"],
            deploymentScript: null,
            TimeSpan.FromSeconds(1.5));

        var text = result.Describe();

        StringAssert.Contains(text, "Applied 1 change(s) for AppDbContext on localhost/AppDb");
        StringAssert.Contains(text, "Create Table [dbo].[T]");
        StringAssert.Contains(text, "[dbo].[Legacy]");
        StringAssert.Contains(text, "Data loss risk: col dropped");
        StringAssert.Contains(text, "Warning: seed skipped");
        Assert.IsTrue(result.HasChanges);
    }
}
