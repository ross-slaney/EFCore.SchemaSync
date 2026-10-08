using System.Runtime.InteropServices;

// Test orchestration: a throwaway SQL Server 2022 container (no persistence) and the sample API.
// The integration tests start this host through Aspire.Hosting.Testing, create their own disposable
// databases on the server, and talk to the sample over HTTP.
var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-password", value: "IntegrationTests_Passw0rd!", secret: true);

var sql = builder.AddSqlServer("sql", sqlPassword)
    .WithImageTag("2022-latest")
    .WithContainerRuntimeArgs("--log-driver=none");

if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
{
    sql.WithContainerRuntimeArgs("--platform", "linux/amd64");
}

var sampleDb = sql.AddDatabase("sampledb");

builder.AddProject<Projects.SchemaSync_Sample>("sample")
    .WithReference(sampleDb)
    .WaitFor(sampleDb)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
