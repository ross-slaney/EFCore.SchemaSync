using EFCore.SchemaSync;
using Microsoft.Extensions.Logging;

namespace SchemaSync.Sample;

/// <summary>
/// <c>dotnet SchemaSync.Sample.dll --apply-schema [--dry-run] [--allow-data-loss] [--allow-object-removal]</c>
/// applies the schema with the same library call the web host uses at startup and exits without starting Kestrel.
/// <c>dotnet SchemaSync.Sample.dll --verify-schema</c> only checks that the database matches the model (for pipelines).
/// Exit codes: 0 success / schema current, 1 schema failure, 2 unexpected failure, 3 schema drift (verify only).
/// </summary>
public static class SchemaCommand
{
    public const string ApplyFlag = "--apply-schema";
    public const string VerifyFlag = "--verify-schema";
    public const int DriftExitCode = 3;

    private static readonly string[] Flags = [ApplyFlag, VerifyFlag, "--dry-run", "--allow-data-loss", "--allow-object-removal"];

    public static bool IsFlag(string arg) => Flags.Contains(arg, StringComparer.OrdinalIgnoreCase);

    public static bool IsRequested(string[] args)
        => args.Contains(ApplyFlag, StringComparer.OrdinalIgnoreCase) || args.Contains(VerifyFlag, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        if (args.Contains(VerifyFlag, StringComparer.OrdinalIgnoreCase))
        {
            return await VerifyAsync(services, cancellationToken);
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SchemaSync.Sample");
        var options = new SchemaSyncOptions
        {
            DryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase),
            AllowDataLoss = args.Contains("--allow-data-loss", StringComparer.OrdinalIgnoreCase),
            AllowObjectRemoval = args.Contains("--allow-object-removal", StringComparer.OrdinalIgnoreCase),
        };
        SampleSchema.Configure(options);

        try
        {
            var result = await services.ApplyDatabaseSchemaAsync<AppDbContext>(options, cancellationToken);
            Console.WriteLine(result.Describe());
            if (result.Outcome == SchemaSyncOutcome.Previewed && result.DeploymentScript is { } script)
            {
                Console.WriteLine();
                Console.WriteLine("-- Deployment script (not executed) --");
                Console.WriteLine(script);
            }

            return 0;
        }
        catch (SchemaSyncException ex)
        {
            logger.LogError(ex, "Schema application failed at stage {Stage}.", ex.Stage);
            Console.Error.WriteLine($"Schema application failed at stage {ex.Stage}: {ex.Message}");
            if (ex.InnerException is { } inner)
            {
                Console.Error.WriteLine($"  Caused by: {inner.GetType().Name}: {inner.Message}");
            }

            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Schema application was cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schema application failed unexpectedly.");
            Console.Error.WriteLine($"Schema application failed unexpectedly: {ex}");
            return 2;
        }
    }

    private static async Task<int> VerifyAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var result = await services.VerifyDatabaseSchemaAsync<AppDbContext>(SampleSchema.Configure, cancellationToken);
            Console.WriteLine(result.Describe());
            return 0;
        }
        catch (SchemaDriftException drift)
        {
            Console.Error.WriteLine(drift.Message);
            if (drift.Result.DeploymentScript is { } script)
            {
                Console.WriteLine("-- Deployment script an application would run --");
                Console.WriteLine(script);
            }

            return DriftExitCode;
        }
        catch (SchemaSyncException ex)
        {
            Console.Error.WriteLine($"Schema verification failed at stage {ex.Stage}: {ex.Message}");
            return 1;
        }
    }
}
