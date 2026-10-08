using System.Diagnostics;
using EFCore.SchemaSync.IntegrationTests.Infrastructure;
using EFCore.SchemaSync.Tests.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EFCore.SchemaSync.IntegrationTests.Tests;

/// <summary>Target-only objects, lossy changes, unsupported constructs, failures, locking, cancellation and logging.</summary>
[TestClass]
public sealed class SafetyTests
{
    private static async Task<TestDatabase> PopulatedAsync(SchemaShape shape, int rows = 3)
    {
        var db = await TestDatabase.CreateAsync();
        await db.ApplyShapeAsync(shape);
        await using var context = db.OpenShape(shape);
        for (var i = 1; i <= rows; i++)
        {
            context.Customers.Add(new EvolvingCustomer { Name = $"Customer {i}", Phone = shape.Phone ? $"555-010{i}" : null });
        }

        await context.SaveChangesAsync();
        return db;
    }

    private static Task AddTargetOnlyObjectsAsync(TestDatabase db) => db.ExecuteAsync(
        "CREATE TABLE dbo.DbaOnly (Id int NOT NULL PRIMARY KEY, Note nvarchar(50) NULL)",
        "CREATE INDEX IX_Dba_Extra ON dbo.Customers (Email)",
        "CREATE TRIGGER dbo.TR_Customers_Audit ON dbo.Customers AFTER INSERT AS BEGIN SET NOCOUNT ON; END",
        "CREATE VIEW dbo.vCustomers AS SELECT Id, Name FROM dbo.Customers",
        "ALTER TABLE dbo.Customers ADD CONSTRAINT CK_Dba CHECK (Priority >= -1000)",
        "CREATE USER [reporting] WITHOUT LOGIN",
        "ALTER ROLE db_datareader ADD MEMBER [reporting]",
        "GRANT SELECT ON dbo.Customers TO [reporting]");

    [TestMethod]
    public async Task Target_only_objects_are_preserved_and_reported()
    {
        await using var db = await PopulatedAsync(SchemaShape.V1);
        await AddTargetOnlyObjectsAsync(db);

        var result = await db.ApplyShapeAsync(SchemaShape.V1);

        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, result.Outcome, result.Describe());
        var retained = result.RetainedObjects.Select(r => r.ToString()).ToList();
        CollectionAssert.Contains(retained, "Table [dbo].[DbaOnly]");
        CollectionAssert.Contains(retained, "Index [dbo].[Customers].[IX_Dba_Extra]");
        CollectionAssert.Contains(retained, "DmlTrigger [dbo].[TR_Customers_Audit]");
        CollectionAssert.Contains(retained, "View [dbo].[vCustomers]");
        CollectionAssert.Contains(retained, "CheckConstraint [dbo].[CK_Dba]");
        Assert.IsFalse(retained.Any(r => r.Contains("reporting", StringComparison.Ordinal)), "security objects are unmanaged, not 'retained'");

        Assert.IsTrue(await db.ObjectExistsAsync("dbo.DbaOnly"));
        Assert.IsNotNull(await SchemaInspector.IndexAsync(db, "dbo.Customers", "IX_Dba_Extra"));
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.TR_Customers_Audit"));
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.vCustomers"));
        Assert.IsTrue(await db.ObjectExistsAsync("dbo.CK_Dba"));
        Assert.AreEqual(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.database_principals WHERE name = 'reporting'"));
        Assert.IsTrue(db.Logs.Library.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("[dbo].[DbaOnly]", StringComparison.Ordinal)), "retained objects are logged as warnings");
    }

    [TestMethod]
    public async Task Opting_into_object_removal_drops_target_only_objects_but_never_security()
    {
        await using var db = await PopulatedAsync(SchemaShape.V1);
        await AddTargetOnlyObjectsAsync(db);

        var result = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { AllowObjectRemoval = true });

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        var changes = result.Changes.Select(c => c.ToString()).ToList();
        CollectionAssert.Contains(changes, "Drop Table [dbo].[DbaOnly]");
        CollectionAssert.Contains(changes, "Drop Index [dbo].[Customers].[IX_Dba_Extra]");
        CollectionAssert.Contains(changes, "Drop DmlTrigger [dbo].[TR_Customers_Audit]");
        CollectionAssert.Contains(changes, "Drop View [dbo].[vCustomers]");
        CollectionAssert.Contains(changes, "Drop CheckConstraint [dbo].[CK_Dba]");
        Assert.AreEqual(0, result.RetainedObjects.Count);

        Assert.IsFalse(await db.ObjectExistsAsync("dbo.DbaOnly"));
        Assert.IsNull(await SchemaInspector.IndexAsync(db, "dbo.Customers", "IX_Dba_Extra"));
        Assert.IsFalse(await db.ObjectExistsAsync("dbo.TR_Customers_Audit"));
        Assert.IsFalse(await db.ObjectExistsAsync("dbo.vCustomers"));
        Assert.IsFalse(await db.ObjectExistsAsync("dbo.CK_Dba"));
        Assert.AreEqual(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.database_principals WHERE name = 'reporting'"), "users survive");
        Assert.AreEqual(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.database_role_members rm JOIN sys.database_principals p ON p.principal_id = rm.member_principal_id WHERE p.name = 'reporting'"), "role membership survives");
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"), "managed data is untouched");
    }

    [TestMethod]
    public async Task Removing_an_entity_keeps_its_table_until_removal_and_data_loss_are_both_allowed()
    {
        var withAudit = SchemaShape.V1 with { AuditLog = true };
        await using var db = await PopulatedAsync(withAudit);
        await db.ExecuteAsync("INSERT INTO audit.AuditEntries (CustomerId, Message, At) SELECT TOP 1 Id, N'hello', SYSUTCDATETIME() FROM dbo.Customers");

        var kept = await db.ApplyShapeAsync(SchemaShape.V1);
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, kept.Outcome, kept.Describe());
        Assert.IsTrue(kept.RetainedObjects.Any(r => r.ObjectType == "Table" && r.Name == "[audit].[AuditEntries]"), kept.Describe());
        Assert.IsTrue(kept.RetainedObjects.Any(r => r.ObjectType == "Schema" && r.Name == "[audit]"), kept.Describe());
        Assert.AreEqual(1, await db.CountAsync("audit.AuditEntries"));

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { AllowObjectRemoval = true }));
        Assert.AreEqual(SchemaSyncStage.Compare, blocked.Stage);
        Assert.IsTrue(blocked.DataLossRisks.Any(r => r.Contains("[audit].[AuditEntries]", StringComparison.Ordinal)), blocked.Message);
        Assert.IsTrue(blocked.PlannedChanges.Any(c => c.Kind == SchemaChangeKind.Drop && c.ObjectName == "[audit].[AuditEntries]"));
        Assert.IsTrue(await db.ObjectExistsAsync("audit.AuditEntries"), "a populated table is never dropped without AllowDataLoss");

        var dropped = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { AllowObjectRemoval = true, AllowDataLoss = true });
        Assert.AreEqual(SchemaSyncOutcome.Applied, dropped.Outcome, dropped.Describe());
        Assert.IsTrue(dropped.DataLossRisks.Count > 0);
        Assert.IsFalse(await db.ObjectExistsAsync("audit.AuditEntries"));
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));
    }

    [TestMethod]
    public async Task Removing_a_property_is_blocked_until_data_loss_is_allowed()
    {
        var withPhone = SchemaShape.V1 with { Phone = true };
        await using var db = await PopulatedAsync(withPhone);

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(SchemaShape.V1));

        Assert.AreEqual(SchemaSyncStage.Compare, blocked.Stage);
        Assert.IsTrue(blocked.DataLossRisks.Any(r => r.Contains("[Phone]", StringComparison.Ordinal)), blocked.Message);
        StringAssert.Contains(blocked.Message, "AllowDataLoss");
        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Customers", "Phone"), "blocked deployments change nothing");
        Assert.AreEqual(3, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Customers WHERE Phone IS NOT NULL"));

        var applied = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { AllowDataLoss = true });
        Assert.AreEqual(SchemaSyncOutcome.Applied, applied.Outcome, applied.Describe());
        Assert.IsTrue(applied.DataLossRisks.Any(r => r.Contains("[Phone]", StringComparison.Ordinal)));
        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "Phone"));
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyShapeAsync(SchemaShape.V1)).Outcome);
    }

    [TestMethod]
    public async Task Narrowing_a_column_is_blocked_until_data_loss_is_allowed()
    {
        await using var db = await PopulatedAsync(SchemaShape.V1);
        var narrower = SchemaShape.V1 with { NameLength = 50 };

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(narrower));
        Assert.IsTrue(blocked.DataLossRisks.Any(r => r.Contains("Name", StringComparison.Ordinal)), blocked.Message);
        Assert.AreEqual(100, (await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Name"))!.Length);

        var applied = await db.ApplyShapeAsync(narrower, new SchemaSyncOptions { AllowDataLoss = true });
        Assert.AreEqual(SchemaSyncOutcome.Applied, applied.Outcome, applied.Describe());
        Assert.AreEqual(50, (await SchemaInspector.ColumnAsync(db, "dbo.Customers", "Name"))!.Length);
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));
    }

    [TestMethod]
    public async Task A_column_added_outside_the_model_counts_as_a_lossy_drop()
    {
        await using var db = await PopulatedAsync(SchemaShape.V1);
        await db.ExecuteAsync("ALTER TABLE dbo.Customers ADD DbaColumn int NULL");

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(SchemaShape.V1));
        Assert.IsTrue(blocked.DataLossRisks.Any(r => r.Contains("[DbaColumn]", StringComparison.Ordinal)), blocked.Message);
        Assert.IsTrue(await db.ColumnExistsAsync("dbo.Customers", "DbaColumn"));

        var applied = await db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { AllowDataLoss = true });
        Assert.AreEqual(SchemaSyncOutcome.Applied, applied.Outcome, applied.Describe());
        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "DbaColumn"));
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));
    }

    [TestMethod]
    public async Task Unsupported_constructs_are_rejected_before_anything_is_modified()
    {
        await using var db = await TestDatabase.CreateAsync();

        var ex = await Assert.ThrowsExactlyAsync<UnsupportedSchemaException>(() => db.ApplyAsync<MemoryOptimizedDbContext>());

        Assert.AreEqual(SchemaSyncStage.ConvertModel, ex.Stage);
        StringAssert.Contains(ex.Message, "memory-optimized");
        Assert.AreEqual(0, await db.TableCountAsync());
    }

    [TestMethod]
    public async Task Insufficient_permissions_fail_with_the_stage_and_cause_and_change_nothing()
    {
        await using var db = await TestDatabase.CreateAsync();
        var login = $"reader_{Guid.NewGuid():N}"[..20];
        const string password = "Reader_Passw0rd!2024";
        await SqlFixture.ExecuteAsync(SqlFixture.ConnectionStringFor("master"), $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
        try
        {
            await db.ExecuteAsync($"CREATE USER [{login}] FOR LOGIN [{login}]", $"ALTER ROLE db_datareader ADD MEMBER [{login}]");
            var readerConnection = new SqlConnectionStringBuilder(db.ConnectionString) { UserID = login, Password = password, IntegratedSecurity = false }.ConnectionString;

            var ex = await Assert.ThrowsAsync<SchemaSyncException>(() => db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { DeploymentConnectionString = readerConnection }));

            Assert.IsTrue(ex.Stage is SchemaSyncStage.Compare or SchemaSyncStage.Deploy, $"stage was {ex.Stage}: {ex.Message}");
            Assert.IsNotNull(ex.InnerException, "the DacFx/SqlClient cause is retained");
            Assert.IsFalse(ex.Message.Contains(password, StringComparison.Ordinal));
            Assert.AreEqual(0, await db.TableCountAsync(), "nothing was created");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await SqlFixture.ExecuteAsync(SqlFixture.ConnectionStringFor("master"), $"""
                DECLARE @kill nvarchar(max) = N'';
                SELECT @kill += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';' FROM sys.dm_exec_sessions WHERE login_name = N'{login}';
                EXEC (@kill);
                IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{login}') DROP LOGIN [{login}];
                """);
        }
    }

    [TestMethod]
    public async Task A_failing_statement_rolls_back_the_whole_deployment()
    {
        await using var db = await PopulatedAsync(SchemaShape.V1);
        await db.ExecuteAsync("UPDATE dbo.Customers SET Email = N'dup@example.test'");
        var shape = SchemaShape.V1 with { UniqueEmailIndex = true, AuditLog = true };

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => db.ApplyShapeAsync(shape));

        Assert.AreEqual(SchemaSyncStage.Deploy, ex.Stage, ex.Message);
        Assert.IsNotNull(ex.InnerException, "the DacFx/SqlClient cause is retained");
        StringAssert.Contains(ex.Message, "IX_Customers_Email");
        Assert.IsFalse(await db.ObjectExistsAsync("audit.AuditEntries"), "the table created earlier in the same deployment was rolled back");
        Assert.IsNull(await SchemaInspector.IndexAsync(db, "dbo.Customers", "IX_Customers_Email"));
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));
        Assert.IsTrue(db.Logs.Library.Any(e => e.Level == LogLevel.Error), "DacFx errors are logged");
    }

    [TestMethod]
    public async Task A_required_column_without_a_default_is_blocked_and_with_data_loss_allowed_fails_and_rolls_back()
    {
        await using var db = await PopulatedAsync(SchemaShape.V1);
        var shape = SchemaShape.V1 with { Mandatory = true, AuditLog = true };

        var blocked = await Assert.ThrowsExactlyAsync<SchemaChangesBlockedException>(() => db.ApplyShapeAsync(shape));
        Assert.IsTrue(blocked.DataLossRisks.Any(r => r.Contains("[Mandatory]", StringComparison.Ordinal)), blocked.Message);
        Assert.IsFalse(await db.ObjectExistsAsync("audit.AuditEntries"));

        var failed = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => db.ApplyShapeAsync(shape, new SchemaSyncOptions { AllowDataLoss = true }));
        Assert.AreEqual(SchemaSyncStage.Deploy, failed.Stage, failed.Message);
        Assert.IsNotNull(failed.InnerException);
        Assert.IsFalse(await db.ObjectExistsAsync("audit.AuditEntries"), "rolled back together with the failing ALTER");
        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "Mandatory"));
        Assert.AreEqual(3, await db.CountAsync("dbo.Customers"));
    }

    [TestMethod]
    public async Task Lossy_changes_on_empty_tables_proceed_and_are_reported_as_risks()
    {
        await using var db = await TestDatabase.CreateAsync();
        var withPhone = SchemaShape.V1 with { Phone = true };
        await db.ApplyShapeAsync(withPhone);

        var result = await db.ApplyShapeAsync(SchemaShape.V1);

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.IsTrue(result.DataLossRisks.Any(r => r.Contains("[Phone]", StringComparison.Ordinal)), "the DacFx risk is still reported");
        Assert.IsFalse(await db.ColumnExistsAsync("dbo.Customers", "Phone"));
        Assert.IsTrue(db.Logs.Library.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("table is empty", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Lock_wait_times_out_when_another_instance_holds_the_lock()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var holder = await HoldLockAsync(db);

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsExactlyAsync<SchemaLockTimeoutException>(() => db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { LockTimeout = TimeSpan.FromSeconds(2) }));

        Assert.AreEqual(SchemaSyncStage.AcquireLock, ex.Stage);
        Assert.AreEqual("EFCore.SchemaSync", ex.ResourceName);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"waited {stopwatch.Elapsed}");
        Assert.AreEqual(0, await db.TableCountAsync(), "nothing was deployed without the lock");

        await holder.DisposeAsync();
        Assert.AreEqual(SchemaSyncOutcome.Applied, (await db.ApplyShapeAsync(SchemaShape.V1)).Outcome, "the lock is released when the holder's session ends");
    }

    [TestMethod]
    public async Task Cancellation_while_waiting_for_the_lock_is_honored()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var holder = await HoldLockAsync(db);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { LockTimeout = TimeSpan.FromMinutes(2) }, cts.Token));

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"cancellation took {stopwatch.Elapsed}");
        Assert.AreEqual(0, await db.TableCountAsync());
    }

    [TestMethod]
    public async Task Concurrent_instances_are_serialized_and_all_succeed()
    {
        await using var db = await TestDatabase.CreateAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => db.ApplyAsync<MatrixDbContext>()));

        Assert.AreEqual(1, results.Count(r => r.Outcome == SchemaSyncOutcome.Applied), string.Join(Environment.NewLine, results.Select(r => r.Describe())));
        Assert.AreEqual(3, results.Count(r => r.Outcome == SchemaSyncOutcome.NoChangesNeeded));
        Assert.IsTrue(await db.ObjectExistsAsync("sales.Orders"));
        Assert.AreEqual(SchemaSyncOutcome.NoChangesNeeded, (await db.ApplyAsync<MatrixDbContext>()).Outcome);
    }

    [TestMethod]
    public async Task Deployment_connection_override_targets_another_database()
    {
        await using var configured = await TestDatabase.CreateAsync();
        await using var target = await TestDatabase.CreateAsync();

        var result = await configured.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { DeploymentConnectionString = target.ConnectionString });

        Assert.AreEqual(SchemaSyncOutcome.Applied, result.Outcome, result.Describe());
        Assert.AreEqual(target.Name, result.DatabaseName);
        Assert.IsTrue(await target.ObjectExistsAsync("dbo.Customers"));
        Assert.AreEqual(0, await configured.TableCountAsync(), "the context's own database is untouched");
    }

    [TestMethod]
    public async Task A_missing_database_fails_at_the_connect_stage_without_creating_it()
    {
        await using var db = await TestDatabase.CreateAsync();
        var missing = SqlFixture.ConnectionStringFor(db.Name + "_missing");

        var ex = await Assert.ThrowsExactlyAsync<SchemaSyncException>(() => db.ApplyShapeAsync(SchemaShape.V1, new SchemaSyncOptions { DeploymentConnectionString = missing }));

        Assert.AreEqual(SchemaSyncStage.Connect, ex.Stage, ex.Message);
        StringAssert.Contains(ex.Message, "does not create databases");
        Assert.AreEqual(0, await SqlFixture.ScalarAsync<int>(SqlFixture.ConnectionStringFor("master"), "SELECT COUNT(*) FROM sys.databases WHERE name = @name", ("@name", db.Name + "_missing")));
    }

    [TestMethod]
    public async Task Logs_describe_progress_and_never_contain_credentials()
    {
        await using var db = await TestDatabase.CreateAsync();

        await db.ApplyAsync<MatrixDbContext>();
        await db.ApplyAsync<MatrixDbContext>();

        var messages = db.Logs.Library.Select(e => e.Message).ToList();
        Assert.IsTrue(messages.Any(m => m.Contains("applying the model of MatrixDbContext", StringComparison.Ordinal)), string.Join(Environment.NewLine, messages));
        Assert.IsTrue(messages.Any(m => m.Contains("converted MatrixDbContext into a DACPAC", StringComparison.Ordinal)));
        Assert.IsTrue(messages.Any(m => m.StartsWith("EFCore.SchemaSync: applied", StringComparison.Ordinal)));
        Assert.IsTrue(messages.Any(m => m.Contains("no changes needed", StringComparison.Ordinal)));
        var password = SqlFixture.SaPassword;
        Assert.IsTrue(password.Length > 0);
        Assert.IsFalse(db.Logs.Entries.Any(e => e.Message.Contains(password, StringComparison.Ordinal) || (e.Exception?.ToString().Contains(password, StringComparison.Ordinal) ?? false)), "credentials must never be logged");
    }

    private static async Task<SqlConnection> HoldLockAsync(TestDatabase db)
    {
        // Pooling is off so that disposing the connection really ends the session and releases the lock.
        var connection = new SqlConnection(new SqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'EFCore.SchemaSync', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 5000; IF @r < 0 THROW 50000, 'could not take the test lock', 1;";
        await command.ExecuteNonQueryAsync();
        return connection;
    }
}
