# Moving from EF Core migrations to EFCore.SchemaSync

This guide takes an application that uses EF Core migrations (`dotnet ef migrations add`, `Database.Migrate()`
or `dotnet ef database update`) to EFCore.SchemaSync, where the EF model itself is the schema. Every step below is
exercised by the integration tests in
[MigrationSwitchTests.cs](../tests/EFCore.SchemaSync.IntegrationTests/Tests/MigrationSwitchTests.cs) against a
database built by real EF migrations.

## What changes, and what does not

- **Your model stays exactly as it is.** Entities, Fluent API configuration and the DbContext are unchanged.
- **Migrations, the model snapshot and `Database.Migrate()` go away.** Instead of replaying recorded steps, every
  start compares the model with the live database and applies the difference with Microsoft DacFx.
- **Your data stays where it is.** A database built by migrations already matches the model, so the first
  deployment usually has nothing to do.
- **`__EFMigrationsHistory` is left alone.** The library treats it like any table it does not own: it is reported as
  a retained object and never changed. Keep it while you might want to go back; drop it later (step 6).

## Checklist

### 1. Bring every environment up to date with the last migration

The switch is simplest when the database already matches the model. Apply every pending migration everywhere
(development, test, staging, production) the usual way, for the last time:

```bash
dotnet ef database update
```

### 2. Replace `Migrate()` with the schema call

```diff
-await using (var scope = app.Services.CreateAsyncScope())
-    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
+await app.Services.ApplyDatabaseSchemaAsync<AppDbContext>();
```

Add the package with `dotnet add package EFCore.SchemaSync`. If migrations ran in a pipeline step instead of at
startup, use the deploy-only pattern from the README (`dotnet MyApp.dll --apply-schema`).

### 3. Preview against a copy of each real database

Before the first real deployment, run a dry run (or the verify API) against staging and, ideally, a restored copy
of production:

```bash
dotnet MyApp.dll --apply-schema --dry-run
```

```csharp
var preview = await app.Services.ApplyDatabaseSchemaAsync<AppDbContext>(o => o.DryRun = true);
```

Expect `NoChangesNeeded` with `[dbo].[__EFMigrationsHistory]` among the retained objects. If the preview lists
changes, they are differences between what your migrations created and what the model says today. Typical ones:

| Preview shows | Why | What to do |
| --- | --- | --- |
| A column or index the model maps but the database lacks | A model change without a migration, or a migration that was never applied | Fine: the deployment adds it. |
| A blocked drop of a column the model no longer maps | A property was removed (or renamed) without a migration | Rename: declare `WasRenamedFrom`. Removal: allow it with `AllowDataLoss` once. |
| Default constraint changes | A migration created the default with a different expression than the model has now (for example `defaultValueSql` edited by hand) | Fine: the deployment recreates the default from the model. |
| Tables, views, procedures or functions created by `migrationBuilder.Sql(...)` | They are not in the model | Reported as retained and left alone. Keep them, or recreate them idempotently in an `AfterDeploy` hook. |

### 4. Deploy

Deploy as usual. The first start compares in full, finds nothing to do (or applies the expected changes from step 3)
and records its state, so later starts with an unchanged model skip the comparison. The result and the log name
`__EFMigrationsHistory` as retained. That is expected.

### 5. Delete the migrations

Remove the `Migrations` folder, including `AppDbContextModelSnapshot.cs`, and the `Microsoft.EntityFrameworkCore.Design`
/ `Microsoft.EntityFrameworkCore.Tools` references if migrations were their only use. From now on a model change is
just a code change:

- **Renames:** `e.Property(c => c.PhoneNumber).WasRenamedFrom("Phone")` (see the README's refactor log section).
  Without it a rename is a blocked drop plus an add, and the error message tells you the line to add.
- **Data fixes that lived in a migration's `Up()`:** move them to a `BeforeDeploy` hook (to fix data so a change
  can succeed) or an `AfterDeploy` hook (to fill a new column). Hooks run on every start, so write them to be
  idempotent.
- **`HasData` seed data:** not deployed by the library; seed it in an `AfterDeploy` hook.

### 6. Remove `__EFMigrationsHistory` when you are sure

Keep the history table while a rollback to migrations is still an option: with the table and the migrations
folder restored, `Migrate()` continues where it stopped, as long as nothing changed the schema in between. Once
you are sure, remove it in one of two ways:

```sql
DROP TABLE [dbo].[__EFMigrationsHistory];
```

or once, from the application, which drops every other target-only object too:

```csharp
await app.Services.ApplyDatabaseSchemaAsync<AppDbContext>(o =>
{
    o.AllowObjectRemoval = true; // drops objects the model does not map...
    o.AllowDataLoss = true;      // ...including tables with rows, like the history table
});
```

Check a dry run with those options first: `AllowObjectRemoval` drops every target-only object, not just the
history table.

## Going back

Restore the `Migrations` folder from source control, put `Migrate()` back, and keep `__EFMigrationsHistory`. EF
applies only migrations newer than the last recorded one, so any schema change made through EFCore.SchemaSync in the
meantime needs a matching migration first (generate one with `dotnet ef migrations add`; its `Up()` may need edits
because the change already exists in the database).
