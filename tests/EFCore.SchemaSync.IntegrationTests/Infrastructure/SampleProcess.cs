using System.Diagnostics;

namespace EFCore.SchemaSync.IntegrationTests.Infrastructure;

/// <summary>Runs the sample application's <c>--apply-schema</c> mode as a real child process, the way an operator would.</summary>
public static class SampleProcess
{
    public sealed record Outcome(int ExitCode, string StandardOutput, string StandardError);

    public static async Task<Outcome> RunAsync(string connectionString, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(LocateSampleDll());
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ConnectionStrings__sampledb"] = connectionString;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment.Remove("ASPNETCORE_URLS");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            Assert.Fail("The sample did not exit within 3 minutes; --apply-schema must never start the web server.");
        }

        return new Outcome(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// The sample must run from its own build output: its deps.json describes its own dependency closure, whereas the
    /// test output mixes in the Aspire packages' (different) SqlClient version.
    /// </summary>
    private static string LocateSampleDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EFCore.SchemaSync.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not locate the repository root to find the sample build output.");
        }

        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var path = Path.Combine(directory.FullName, "samples", "SchemaSync.Sample", "bin", configuration, "net10.0", "SchemaSync.Sample.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException("Build the sample first (it is built with the solution).", path);
    }
}
