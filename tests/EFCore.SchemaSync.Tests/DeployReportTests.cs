using EFCore.SchemaSync.Deployment;

namespace EFCore.SchemaSync.Tests;

[TestClass]
public sealed class DeployReportTests
{
    private const string SampleXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02">
          <Alerts>
            <Alert Name="DataIssue">
              <Issue Value="The column [dbo].[Customers].[Notes] is being dropped, data loss could occur." Id="1" />
              <Issue Value="The type for column Name in table [dbo].[Customers] is currently  NVARCHAR (200) NOT NULL but is being changed to  NVARCHAR (50) NOT NULL." Id="2" />
            </Alert>
            <Alert Name="SqlCmdVariable">
              <Issue Value="Something else" Id="3" />
            </Alert>
          </Alerts>
          <Operations>
            <Operation Name="Create">
              <Item Value="[sales]" Type="SqlSchema" />
              <Item Value="[sales].[Orders]" Type="SqlTable" />
            </Operation>
            <Operation Name="Alter">
              <Item Value="[dbo].[Customers]" Type="SqlTable"><Issue Id="1" /><Issue Id="2" /></Item>
            </Operation>
            <Operation Name="Drop">
              <Item Value="[dbo].[Customers].[IX_Old]" Type="SqlIndex" />
            </Operation>
            <Operation Name="TableRebuild">
              <Item Value="[dbo].[Payments]" Type="SqlTable" />
            </Operation>
            <Operation Name="DropSystemVersioning">
              <Item Value="[dbo].[Employees]" Type="SqlTable" />
            </Operation>
          </Operations>
        </DeploymentReport>
        """;

    [TestMethod]
    public void Parses_operations_and_alerts()
    {
        var report = DeployReport.Parse(SampleXml);

        Assert.AreEqual(6, report.Operations.Count);
        Assert.AreEqual(new SchemaChange(SchemaChangeKind.Create, "Schema", "[sales]", "Create"), report.Operations[0]);
        Assert.AreEqual(new SchemaChange(SchemaChangeKind.Create, "Table", "[sales].[Orders]", "Create"), report.Operations[1]);
        Assert.AreEqual(new SchemaChange(SchemaChangeKind.Alter, "Table", "[dbo].[Customers]", "Alter"), report.Operations[2]);
        Assert.AreEqual(new SchemaChange(SchemaChangeKind.Drop, "Index", "[dbo].[Customers].[IX_Old]", "Drop"), report.Operations[3]);
        Assert.AreEqual(new SchemaChange(SchemaChangeKind.TableRebuild, "Table", "[dbo].[Payments]", "TableRebuild"), report.Operations[4]);
        Assert.AreEqual(new SchemaChange(SchemaChangeKind.Other, "Table", "[dbo].[Employees]", "DropSystemVersioning"), report.Operations[5]);

        Assert.AreEqual(2, report.Alerts.Count);
        Assert.AreEqual(2, report.DataIssues.Count);
        StringAssert.Contains(report.DataIssues[0], "[Notes] is being dropped");
        Assert.AreEqual("Something else", report.Alerts[1].Issues[0]);
    }

    [TestMethod]
    public void Empty_report_has_no_operations()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <DeploymentReport xmlns="http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02">
              <Alerts />
              <Operations />
            </DeploymentReport>
            """;

        var report = DeployReport.Parse(xml);

        Assert.AreEqual(0, report.Operations.Count);
        Assert.AreEqual(0, report.DataIssues.Count);
    }

    [TestMethod]
    public void Schema_change_to_string_is_readable()
    {
        Assert.AreEqual("Create Table [sales].[Orders]", new SchemaChange(SchemaChangeKind.Create, "Table", "[sales].[Orders]", "Create").ToString());
    }
}
