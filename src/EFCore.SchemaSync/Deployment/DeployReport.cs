using System.Xml.Linq;

namespace EFCore.SchemaSync.Deployment;

/// <summary>A DacFx deployment report (the XML produced by <c>DacServices.GenerateDeployReport</c>) in structured form.</summary>
internal sealed class DeployReport
{
    private const string Namespace = "http://schemas.microsoft.com/sqlserver/dac/DeployReport/2012/02";

    private DeployReport(IReadOnlyList<SchemaChange> operations, IReadOnlyList<DeployAlert> alerts, IReadOnlyList<DataIssue> dataIssues)
    {
        Operations = operations;
        Alerts = alerts;
        DataIssueDetails = dataIssues;
    }

    public IReadOnlyList<SchemaChange> Operations { get; }

    public IReadOnlyList<DeployAlert> Alerts { get; }

    /// <summary>Issues DacFx raised under the "DataIssue" alert (possible data loss), with the operations they are attached to.</summary>
    public IReadOnlyList<DataIssue> DataIssueDetails { get; }

    /// <summary>The "DataIssue" messages.</summary>
    public IReadOnlyList<string> DataIssues => DataIssueDetails.Select(i => i.Message).ToArray();

    public static DeployReport Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new FormatException("The deployment report is empty.");
        XNamespace ns = root.Name.Namespace == XNamespace.None ? Namespace : root.Name.Namespace;

        var alerts = root.Elements(ns + "Alerts").Elements(ns + "Alert")
            .Select(alert => new DeployAlert(
                alert.Attribute("Name")?.Value ?? "Unknown",
                alert.Elements(ns + "Issue").Select(issue => issue.Attribute("Value")?.Value ?? string.Empty).Where(v => v.Length > 0).ToArray()))
            .ToArray();

        var operationsWithIssues = root.Elements(ns + "Operations").Elements(ns + "Operation")
            .SelectMany(operation =>
            {
                var name = operation.Attribute("Name")?.Value ?? "Unknown";
                return operation.Elements(ns + "Item").Select(item => (
                    Change: new SchemaChange(
                        ToKind(name),
                        StripSqlPrefix(item.Attribute("Type")?.Value ?? "Unknown"),
                        item.Attribute("Value")?.Value ?? string.Empty,
                        name),
                    IssueIds: item.Elements(ns + "Issue").Select(issue => issue.Attribute("Id")?.Value ?? string.Empty).Where(id => id.Length > 0).ToArray()));
            })
            .ToArray();

        var dataIssues = root.Elements(ns + "Alerts").Elements(ns + "Alert")
            .Where(alert => string.Equals(alert.Attribute("Name")?.Value, "DataIssue", StringComparison.OrdinalIgnoreCase))
            .SelectMany(alert => alert.Elements(ns + "Issue"))
            .Select(issue =>
            {
                var id = issue.Attribute("Id")?.Value ?? string.Empty;
                var affected = operationsWithIssues.Where(o => id.Length > 0 && o.IssueIds.Contains(id, StringComparer.Ordinal)).Select(o => o.Change).ToArray();
                return new DataIssue(id, issue.Attribute("Value")?.Value ?? string.Empty, affected);
            })
            .Where(issue => issue.Message.Length > 0)
            .ToArray();

        return new DeployReport(operationsWithIssues.Select(o => o.Change).ToArray(), alerts, dataIssues);
    }

    internal static SchemaChangeKind ToKind(string operationName) => operationName switch
    {
        "Create" => SchemaChangeKind.Create,
        "Alter" => SchemaChangeKind.Alter,
        "Drop" => SchemaChangeKind.Drop,
        "TableRebuild" => SchemaChangeKind.TableRebuild,
        _ => SchemaChangeKind.Other,
    };

    internal static string StripSqlPrefix(string type)
        => type.StartsWith("Sql", StringComparison.Ordinal) && type.Length > 3 ? type[3..] : type;
}

internal sealed record DeployAlert(string Name, IReadOnlyList<string> Issues);

/// <summary>A DacFx data-loss issue and the planned operations it is attached to (normally one table operation).</summary>
internal sealed record DataIssue(string Id, string Message, IReadOnlyList<SchemaChange> AffectedOperations);
