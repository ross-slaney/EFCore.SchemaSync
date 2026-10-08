# EFCore.SchemaSync

**Your EF model is your schema. No migration files.**

Deploy your EF Core model directly to SQL Server using Microsoft DacFx—with one method call.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/EFCore.SchemaSync)](https://www.nuget.org/packages/EFCore.SchemaSync)
[![CI](https://github.com/ross-slaney/EFCore.SchemaSync/actions/workflows/ci.yml/badge.svg)](https://github.com/ross-slaney/EFCore.SchemaSync/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com)

<p align="center">
  <img src="https://raw.githubusercontent.com/ross-slaney/EFCore.SchemaSync/main/docs/hero.png" alt="A Customer entity in AppDbContext.cs mapped property by property to the dbo.Customers table in SQL Server: EFCore.SchemaSync turns the model into a DACPAC and DacFx compares and deploys it." width="920">
</p>

```csharp
var app = builder.Build();

await app.Services.ApplyDatabaseSchemaAsync<AppDbContext>();

await app.RunAsync();
```

That call reads the desired schema from your EF Core model, builds a DACPAC in memory, compares it with the live
database using DacFx, and applies the difference. Success lets startup continue. Failure throws and stops startup.

Your entities and Fluent API configuration are the only schema definition you maintain. There are no migration
files, model snapshots, SQL scripts, SQL projects, extra CLI tools, or scratch databases.

## Contents

- [How it works](#how-it-works)
- [Getting started](#getting-started)
- [Database permissions](#database-permissions)
- [What happens to things that are not in the model](#what-happens-to-things-that-are-not-in-the-model)
- [Defaults and options](#defaults-and-options)
- [Results and errors](#results-and-errors)
- [Concurrency: the schema lock](#concurrency-the-schema-lock)
- [Renames and schema moves (refactor log)](#renames-and-schema-moves-refactor-log)
- [Supported mappings (tested)](#supported-mappings-tested)
- [Limitations](#limitations)
- [Sample application and Aspire](#sample-application-and-aspire)
- [Tests](#tests)
- [Releasing to NuGet](#releasing-to-nuget)

## How it works

1. **Resolve.** A new DI scope is created, your `DbContext` is resolved, the provider is checked (SQL Server only)
   and the connection configuration is read from the context (or from `SchemaSyncOptions.DeploymentConnectionString`).
2. **Connect and lock.** One connection to the target database is opened. It reads the database collation and holds
   a database-scoped exclusive application lock (`sp_getapplock`) for the rest of the operation so that several
   application instances starting at once never compare or deploy concurrently.
3. **Convert.** EF Core generates its create script for the model. The script is parsed with
   `Microsoft.SqlServer.TransactSql.ScriptDom` (no regular expressions) and normalized into declarative statements:
   `IF SCHEMA_ID(...) EXEC(N'CREATE SCHEMA ...')` becomes `CREATE SCHEMA`, temporal tables and comments lose their
   dynamic SQL, seed data is skipped with a warning, and every expression (defaults, computed columns, check
   constraints, index filters) is rewritten into exactly the form SQL Server stores so that a second run reports no
   differences. Anything the normalizer does not recognize is rejected with `UnsupportedSchemaException`; nothing is
   silently dropped. DacFx then builds and validates the model and produces the DACPAC in memory.
4. **Compare.** DacFx generates a deployment report against the live database. Possible data loss blocks the
   operation unless you opted in. A second report tells you which target-only objects were preserved.
5. **Deploy or preview.** DacFx deploys the plan inside a transaction with deployment verification on, or returns
   the deployment script on a dry run.

EF migrations, the migrations history table and model snapshots are not used at all.

## Getting started

Requirements: .NET 10, EF Core 10 (`Microsoft.EntityFrameworkCore.SqlServer`), SQL Server 2022 (tested; newer
engines are accepted, older ones need `AllowIncompatiblePlatform`). The target database must already exist; the
library never creates databases.

```bash
dotnet add package EFCore.SchemaSync
```

Register your context as usual and apply the schema after building the host, before serving requests:

```csharp
using EFCore.SchemaSync;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("app")));

var app = builder.Build();

await app.Services.ApplyDatabaseSchemaAsync<AppDbContext>();   // throws on failure: the host never starts

await app.RunAsync();
```

With options, a cancellation token and the structured result:

```csharp
var result = await app.Services.ApplyDatabaseSchemaAsync<AppDbContext>(new SchemaSyncOptions
{
    DryRun = args.Contains("--dry-run"),
    LockTimeout = TimeSpan.FromMinutes(5),
    AllowDataLoss = app.Environment.IsDevelopment(),
}, cancellationToken);

Console.WriteLine(result.Describe());
// Applied 3 change(s) for AppDbContext on tcp:sql.example.net,1433/app in 4.2s.
//   Create Table [app].[Lists]
//   Create Table [app].[Items]
//   Create Index [app].[Items].[IX_Items_DueDate]
```

Without dependency injection: `SchemaSync.ApplyAsync(dbContext, options, logger, cancellationToken)`.

### Deploy-only mode for pipelines

Applying at startup is convenient, but you may prefer to run the deployment as a separate step (for example before
a rolling deployment). The sample shows the pattern: the same library call, exit code 0 or 1, and the web server
is never started:

```bash
dotnet MyApp.dll --apply-schema            # apply and exit
dotnet MyApp.dll --apply-schema --dry-run  # print the plan and the SQL script, change nothing
```

See [samples/SchemaSync.Sample/SchemaCommand.cs](samples/SchemaSync.Sample/SchemaCommand.cs). No separate CLI is
installed; it is your application binary.

## Database permissions

The login used for the deployment (the context's connection, or `DeploymentConnectionString`) needs:

| Permission | Why |
| --- | --- |
| `CONNECT` to the existing database | The library never creates databases. |
| `VIEW DEFINITION` on the database | DacFx reads the live schema to compare. Included in `db_ddladmin`/`db_owner`. |
| `CREATE SCHEMA`, `CREATE TABLE`, `ALTER` on managed schemas, `CREATE SEQUENCE` | Deploying the model. `db_ddladmin` covers all of it. |
| `sp_getapplock` | Requires nothing beyond `public`. |

The simplest working setup is a login that is a member of `db_ddladmin` plus `db_datareader`/`db_datawriter`, or
`db_owner`. `sysadmin` is not needed. If the application itself runs with least privilege, pass an elevated
connection through `SchemaSyncOptions.DeploymentConnectionString`; the context's own connection is then only used
to identify the model.

Azure AD / Entra ID access tokens set on the context's `SqlConnection` are forwarded to DacFx.

## What happens to things that are not in the model

| Situation | Default behavior | Opt-in |
| --- | --- | --- |
| A table, view, procedure, function, trigger, index, constraint or sequence exists in the database but not in the model (for example you removed an entity, a DBA added an index, or `__EFMigrationsHistory` is left over) | Preserved. Listed in `SchemaSyncResult.RetainedObjects` and logged as a warning. | `AllowObjectRemoval = true` drops them. Dropping a table that contains rows additionally needs `AllowDataLoss`; an empty table is dropped with `AllowObjectRemoval` alone. |
| A column exists in a managed table but not in the model (you removed a property, or someone added a column by hand) | Blocked with `SchemaChangesBlockedException` before anything runs when the table contains rows, because DacFx cannot partially manage a table and dropping the column is lossy. On an empty table the column is dropped and the risk is listed in `DataLossRisks`. | `AllowDataLoss = true` drops the column regardless. |
| A column is narrowed or its type changed in a way that may truncate (`nvarchar(200)` to `nvarchar(50)`, `bigint` to `int`), or a required column without a default is added | Blocked with `SchemaChangesBlockedException` when the table contains rows. | `AllowDataLoss = true`. SQL Server may still reject the statement at run time (for example a required column without a default on a populated table); the deployment then fails at the `Deploy` stage and rolls back. |
| A property, table or schema is renamed | Without a hint EF has no rename information, so this is a drop plus an add and is blocked like a removal. | Declare the previous name with `WasRenamedFrom` (or an explicit `RefactorLog`) and DacFx renames in place, keeping the data. See [Renames and schema moves](#renames-and-schema-moves-refactor-log). |
| Users, roles, role membership, permissions, logins, credentials, keys, certificates, audits, filegroups, files, database options and other database-level configuration | Never compared, never changed, never dropped. | None. These are always unmanaged. |
| Index storage options (fill factor, padding, compression, lock hints), column order, table options, partitioning, replication flags, `WITH NOCHECK` | Ignored during comparison, so DBA tuning survives and a column added in the middle of a class is appended with `ALTER TABLE ADD` instead of rebuilding the table. | `ConfigureDeployOptions` if you really want DacFx to manage them. |
| Seed data (`HasData`) | Not deployed. The result and the log carry a warning. | None. Seed separately. |

The rule is the one DacFx itself applies when it executes with `BlockOnPossibleDataLoss`: a flagged change only blocks when the affected table holds rows. The library checks that before running any DDL so the failure is deterministic, and DacFx checks it again at execution time. Dry runs never block: they return `Previewed` with the risks listed in `SchemaSyncResult.DataLossRisks`.

## Defaults and options

Every default is the safe one. `SchemaSyncOptions`:

| Option | Default | Meaning |
| --- | --- | --- |
| `DryRun` | `false` | Compare and script only. Nothing is executed. |
| `DeploymentConnectionString` | context's connection | Connection used for the lock, comparison and deployment. Must name a database. |
| `LockTimeout` | 2 minutes | Bounded wait for the schema lock held by another instance. Exceeding it throws `SchemaLockTimeoutException`. |
| `CommandTimeout` | 5 minutes | Per-statement timeout DacFx uses during comparison and deployment. |
| `LongRunningCommandTimeout` | same as `CommandTimeout` | DacFx timeout for long-running statements such as index builds. |
| `AllowObjectRemoval` | `false` | Drop target-only objects (see above). Security objects are never dropped. |
| `AllowDataLoss` | `false` | Permit changes DacFx flags as potentially lossy. |
| `ReportRetainedObjects` | `true` | Run the extra comparison that lists preserved target-only objects. |
| `UseTransaction` | `true` | Transactional deployment script (`SET XACT_ABORT ON`, one transaction). SQL Server cannot roll back every DDL statement, so this is best effort, not a universal guarantee. |
| `IncludeDeploymentScript` | `false` | Also return the executed script on real deployments. Dry runs always return it. |
| `TargetSqlServerVersion` | `Sql160` (SQL Server 2022) | DacFx target platform of the generated DACPAC. |
| `AllowIncompatiblePlatform` | `false` | Let DacFx deploy to a different platform (for example Azure SQL Database with `SqlAzure`). |
| `LockResourceName` | `EFCore.SchemaSync` | `sp_getapplock` resource name, scoped to the target database. |
| `RefactorLog` | `null` | Explicit renames and schema moves applied in place (see below), after the ones derived from model annotations. |
| `RefactorLogPath` | `null` | An SSDT-style `.refactorlog` file whose operations are applied last. |
| `ConfigureDeployOptions` | `null` | Escape hatch run last on the DacFx `DacDeployOptions`. |

What DacFx is configured with by default: `BlockOnPossibleDataLoss`, `VerifyDeployment`,
`IncludeTransactionalScripts`, no `Drop*NotInSource`, security and database settings excluded (`ExcludeObjectTypes`
plus `IgnorePermissions`, `IgnoreRoleMembership`, `IgnoreUserSettingsObjects`, `IgnoreAuthorizer`,
`ScriptDatabaseOptions = false`), `IgnoreColumnOrder`, `IgnoreIndexOptions`, `IgnoreFillFactor`,
`IgnoreTableOptions`, `IgnorePartitionSchemes`, `IgnoreWithNocheck*`, `GenerateSmartDefaults = false`,
`CreateNewDatabase = false`, `BackupDatabaseBeforeChanges = false`. See
[DacDeployOptionsFactory.cs](src/EFCore.SchemaSync/Deployment/DacDeployOptionsFactory.cs).

## Results and errors

`SchemaSyncResult`:

| Member | Content |
| --- | --- |
| `Outcome` | `NoChangesNeeded`, `Previewed` (dry run with differences) or `Applied`. |
| `Changes` | The DacFx operations planned or executed: `Create`/`Alter`/`Drop`/`TableRebuild`/`Rename`/`MoveSchema`/other, object type and name. |
| `RetainedObjects` | Target-only objects that were preserved. |
| `DataLossRisks` | Every data-loss warning DacFx raised for the plan. Populated on dry runs, when `AllowDataLoss` is on, or when the affected tables were empty (otherwise the call throws `SchemaChangesBlockedException`). |
| `Warnings` | Conversion and comparison warnings, for example skipped seed data. |
| `DeploymentScript` | The SQLCMD-mode script (dry runs, or `IncludeDeploymentScript`). |
| `ContextName`, `ServerName`, `DatabaseName`, `Duration` | What was applied where, and how long it took including the lock wait. |
| `Describe()` | Multi-line summary for logs and consoles. |

All failures derive from `SchemaSyncException`. `Stage` says where the operation failed (`ResolveContext`,
`ConvertModel`, `BuildPackage`, `Connect`, `AcquireLock`, `Compare`, `Deploy`) and `InnerException` keeps the DacFx,
SqlClient or EF Core cause. Specialized subclasses:

| Exception | Stage | Meaning |
| --- | --- | --- |
| `UnsupportedSchemaException` | `ConvertModel` | The model emits constructs the library cannot deploy declaratively. `UnsupportedConstructs` lists all of them. Nothing was touched. |
| `SchemaLockTimeoutException` | `AcquireLock` | Another instance held the lock for longer than `LockTimeout`. |
| `SchemaChangesBlockedException` | `Compare` | Possible data loss on a table that contains rows and `AllowDataLoss` is off. `DataLossRisks` lists the blocking issues and `PlannedChanges` the whole plan. Nothing was executed. |

Cancellation surfaces as `OperationCanceledException`. Progress, DacFx messages, retained objects and data-loss
warnings are written to `ILogger` category `EFCore.SchemaSync`. Connection strings and credentials are never logged.

## Concurrency: the schema lock

Before comparing, the library takes an exclusive `sp_getapplock` (`@LockOwner = 'Session'`, resource
`EFCore.SchemaSync`) on a dedicated connection to the target database and keeps that connection open until the
deployment has finished or failed. Other instances wait up to `LockTimeout`, then throw. Because the lock is owned by
the session, it is released even if the process dies mid-deployment: the server releases it when the connection goes
away. Instances that waited find no differences left and return `NoChangesNeeded`.

## Renames and schema moves (refactor log)

EF Core cannot tell a renamed property from a removed one plus a new one, so a plain rename deploys as a drop
and an add, which the library blocks as data loss. SQL Server Data Tools projects solve this with a
*refactor log*: a list of renames DacFx applies in place with `sp_rename` and `ALTER SCHEMA ... TRANSFER`
before it compares the rest of the schema. EFCore.SchemaSync embeds the same log into the DACPAC it builds,
in three ways that can be combined.

**Annotations in the model** (the usual way): record the previous name next to the current one.

```csharp
modelBuilder.Entity<Customer>(e =>
{
    e.ToTable("Clients", "crm").WasRenamedFrom("Customers", "dbo");   // table rename + schema move
    e.Property(c => c.PhoneNumber).WasRenamedFrom("Phone");             // column rename
});
```

`WasMovedFromSchema("dbo")` covers a pure schema move. The converter emits column renames first (against the
table's previous name), then table renames, then schema moves, so the operations are always consistent.

**An explicit log** for operations the model cannot express, such as renaming indexes or constraints whose
names changed with their table, or chains of renames. Operations run in order and name objects as they are
called at that point in the sequence:

```csharp
new SchemaSyncOptions
{
    RefactorLog = new RefactorLog()
        .RenameTable("dbo", "Customers", "Clients")
        .RenameIndex("dbo", "Clients", "IX_Customers_Email", "IX_Clients_Email")
        .RenameConstraint("dbo", "Clients", RefactorConstraintKind.PrimaryKey, "PK_Customers", "PK_Clients"),
}
```

**An SSDT `.refactorlog` file** through `SchemaSyncOptions.RefactorLogPath`, if you keep one.

How it behaves:

- Every operation has a key. Annotations and the fluent API derive the key from the operation's content, so
  the same rename yields the same key on every startup. DacFx records applied keys in `dbo.__RefactorLog`
  (it creates the table) and never runs an operation twice. Keys from an imported file are kept as they are.
- On a database where the old object does not exist (a fresh database, or one that was already renamed by
  hand) the operation is skipped and its key is still recorded. Keeping an annotation around is therefore
  harmless; remove it once every environment has been upgraded if you prefer a tidy model.
- Renames show up in `SchemaSyncResult.Changes` as `Rename` and `MoveSchema` and are never data-loss risks.
  Dry runs script them as `sp_rename` calls without executing anything.
- After a table or column rename EF's conventional names change too (`PK_Customers` becomes `PK_Clients`,
  `IX_Customers_Email` becomes `IX_Clients_Email`, `FK_Orders_Customers_CustomerId` becomes
  `FK_Orders_Clients_CustomerId`). The annotations rename those primary keys, alternate keys, indexes and
  foreign keys in place as well, as long as their current name follows EF's convention. Constraints and
  indexes with custom names are left alone; rename them with explicit `RenameIndex`/`RenameConstraint`
  operations if their names changed.
- `dbo.__RefactorLog` belongs to DacFx: it is not reported as a retained object and `AllowObjectRemoval`
  never drops it.

## Supported mappings (tested)

Every row is exercised by the integration tests against a real SQL Server 2022 container: the object is created in
an empty database, inspected through the catalog views, and a second apply must report no changes.

| Category | EF Core mapping | Status |
| --- | --- | --- |
| Tables and schemas | `ToTable(name)`, `ToTable(name, schema)`, `HasDefaultSchema` | Tested |
| Column types | `int`, `long`, `short`, `byte`, `bool`, `decimal(p,s)`, `double`, `float`, `string` (`nvarchar(n)`, `nvarchar(max)`, `nchar`, `varchar`), `byte[]` (`varbinary(n)`, `rowversion`), `Guid`, `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset`, enums as `int` or `string` | Tested |
| Nullability, length, precision, scale | required/optional, `HasMaxLength`, `HasPrecision`, `HasColumnType`, `IsFixedLength`, `IsUnicode(false)` | Tested |
| Column attributes | `UseCollation`, `IsSparse`, `IsRowVersion` | Tested |
| Primary keys | clustered, `IsClustered(false)`, composite, string keys | Tested |
| Foreign keys | `OnDelete` Cascade / Restrict / NoAction / ClientSetNull, self-referencing, cross-schema, cyclic (emitted as `ALTER TABLE ADD CONSTRAINT`) | Tested |
| Alternate keys | `HasAlternateKey` (unique constraint) | Tested |
| Check constraints | `HasCheckConstraint`, including `IN (...)` lists | Tested |
| Identity | default, `UseIdentityColumn(seed, increment)` | Tested |
| Defaults | `HasDefaultValue` for strings, ints, negative numbers, `long` beyond the int range, decimals, doubles, floats, bools, `DateTime`, enums; `HasDefaultValueSql` for `GETUTCDATE()`, `NEWID()`, `NEXT VALUE FOR` | Tested |
| Computed columns | `HasComputedColumnSql` persisted and virtual, including `CAST`/`CONVERT`, arithmetic, string concatenation, `CASE`, `IIF`, `YEAR` | Tested |
| Indexes | plain, `IsUnique`, `HasFilter`, composite, `IsDescending`, `IncludeProperties`, `HasFillFactor` | Tested |
| Sequences | `HasSequence`, HiLo and `NEXT VALUE FOR` defaults | Tested |
| Owned types | `OwnsOne` (columns in the owner's table) | Tested |
| Inheritance | TPH with `HasDiscriminator` | Tested |
| Temporal tables | `IsTemporal()` with the default history table | Tested (creation, idempotency, adding a column with history preserved) |
| Comments | `HasComment` on tables and columns (extended properties) | Tested |
| Seed data | `HasData` | Skipped with a warning (schema deploys) |

Other mappings built from the same primitives (TPT and TPC inheritance, many-to-many join entities, table splitting,
JSON columns stored as `nvarchar(max)`, HiLo) are expected to work but are not part of the test matrix.

Explicitly rejected (`UnsupportedSchemaException`, before anything is executed):

- Memory-optimized tables (`IsMemoryOptimized()`): EF emits database filegroup configuration that is out of scope.
- Any other statement in EF's create script the normalizer does not recognize (views, procedures, triggers,
  `ALTER DATABASE`, arbitrary dynamic SQL). The message lists every offending construct.

## Limitations

- **SQL Server only**, one `DbContext` per database schema. Two contexts deploying into the same database would each
  treat the other's tables as target-only objects (preserved, but reported every time).
- **No rename inference, backfills or seeding.** Renames must be declared (see the refactor log section); undeclared renames are drop-and-add. Data migrations stay your responsibility.
- **Views, functions, procedures and triggers are not created** because EF Core does not create them either. Entities
  mapped with `ToView` need the view to exist already. Existing ones are preserved.
- **Adding a required column without a default to a populated table fails** at the `Deploy` stage (SQL Server rejects
  it) and the transaction rolls back. Give the column a default or make it nullable first.
- **Transactions are best effort.** DacFx wraps the deployment, but some operations (full-text, memory-optimized,
  certain `ALTER DATABASE` statements) cannot be rolled back by SQL Server. The dry-run script shows exactly what runs.
- **The deployment script is SQLCMD-mode T-SQL** (`:setvar`, `:on error exit`). Run it with `sqlcmd` or SSMS in
  SQLCMD mode if you execute it by hand.
- **Azure SQL Database and SQL Server 2019 and earlier are not tested.** Set `TargetSqlServerVersion` and, if DacFx
  objects, `AllowIncompatiblePlatform`.
- **Startup cost.** Converting the model and running the DacFx comparison takes a few seconds (two comparisons when
  `ReportRetainedObjects` is on). DacFx brings a number of transitive package dependencies.

## Sample application and Aspire

[samples/SchemaSync.Sample](samples/SchemaSync.Sample) is a minimal API (todo lists) whose only schema definition is
[AppDbContext.cs](samples/SchemaSync.Sample/AppDbContext.cs). [samples/SchemaSync.Sample.AppHost](samples/SchemaSync.Sample.AppHost)
is an Aspire app host that starts a SQL Server 2022 container and the API. You need .NET 10 and a container runtime
(Docker Desktop or Podman) running in the background; you never run `docker` yourself.

```bash
dotnet run --project samples/SchemaSync.Sample.AppHost
```

The Aspire dashboard opens; the `sample` resource applies the schema at startup and serves `/lists`. The container
and its data volume persist between runs, so edit the model, restart, and watch the upgrade happen. To run the
deploy-only mode against that database, copy the `sampledb` connection string from the dashboard:

```bash
ConnectionStrings__sampledb="Server=127.0.0.1,PORT;Database=sampledb;User ID=sa;Password=...;TrustServerCertificate=true" \
  dotnet samples/SchemaSync.Sample/bin/Debug/net10.0/SchemaSync.Sample.dll --apply-schema --dry-run
```

On Apple silicon the SQL Server image runs through Docker's amd64 emulation (the app hosts add `--platform linux/amd64`
automatically).

## Tests

- [tests/EFCore.SchemaSync.Tests](tests/EFCore.SchemaSync.Tests): unit tests for schema conversion (no database):
  normalization, expression canonicalization, DACPAC construction, option mapping, report parsing, and the public API's
  failure stages.
- [tests/EFCore.SchemaSync.IntegrationTests](tests/EFCore.SchemaSync.IntegrationTests): real SQL Server 2022 through
  `Aspire.Hosting.Testing`. The test app host
  ([tests/EFCore.SchemaSync.IntegrationTests.AppHost](tests/EFCore.SchemaSync.IntegrationTests.AppHost)) starts a
  throwaway container plus the sample API; every test creates and drops its own database. Covered: creation of every
  mapping category and idempotency, upgrades of populated databases with data preservation, dry runs, retained objects
  and object removal, blocked lossy changes, unsupported constructs, permission failures, deployment errors with
  rollback, lock timeouts, cancellation, concurrent instances, connection overrides, logging hygiene, the sample API
  end to end, and the sample's `--apply-schema` process (exit codes, dry run, failure).

```bash
dotnet test tests/EFCore.SchemaSync.Tests
dotnet test tests/EFCore.SchemaSync.IntegrationTests   # needs a container runtime running
```

The [CI workflow](.github/workflows/ci.yml) runs both suites on every pull request and push to `main`, then packs the
library.

## Releasing to NuGet

Create a GitHub release whose tag is the version (`v1.2.0`). Publishing the release triggers
[publish.yml](.github/workflows/publish.yml), which builds, runs the unit and integration tests against a real SQL
Server, packs `EFCore.SchemaSync.1.2.0.nupkg` (plus symbols), pushes it to nuget.org with the `NUGET_API_KEY`
repository secret and attaches the package to the release. Nothing is published from local machines.

## License

MIT. See [LICENSE](LICENSE).
