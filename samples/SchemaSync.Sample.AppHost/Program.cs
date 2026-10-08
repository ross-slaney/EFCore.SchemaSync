using System.Runtime.InteropServices;

// Local development orchestration: one SQL Server 2022 container plus the sample API.
// Run with `dotnet run --project samples/SchemaSync.Sample.AppHost` (or `aspire run`).
// The container and its data volume persist between runs so schema upgrades can be exercised.
var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-password", value: "LocalDevPassword123!", secret: true);

var sql = builder.AddSqlServer("sql", sqlPassword)
    .WithImageTag("2022-latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("schemasync-sample-sql-data");

if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
{
    // The SQL Server image is amd64-only; Docker Desktop on Apple silicon runs it through emulation.
    sql.WithContainerRuntimeArgs("--platform", "linux/amd64");
}

var sampleDb = sql.AddDatabase("sampledb");

builder.AddProject<Projects.SchemaSync_Sample>("sample")
    .WithReference(sampleDb)
    .WaitFor(sampleDb)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

builder.Build().Run();
