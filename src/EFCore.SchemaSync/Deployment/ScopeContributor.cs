using Microsoft.SqlServer.Dac.Deployment;
using Microsoft.SqlServer.Dac.Model;

namespace EFCore.SchemaSync.Deployment;

/// <summary>
/// DacFx deployment plan modifier that keeps objects outside the context's <see cref="DeploymentScope"/>: it removes
/// their drop steps (and the runtime "deployment will halt if the table contains data" checks DacFx generated for them)
/// before DacFx reports, scripts or executes the plan. DacFx loads it from this assembly in-process.
/// </summary>
[ExportDeploymentPlanModifier(ContributorId, "1.0.0.0")]
public sealed class ScopeContributor : DeploymentPlanModifier
{
    /// <summary>Contributor id passed to DacFx.</summary>
    public const string ContributorId = "EFCore.SchemaSync.Scope";

    /// <summary>Argument that carries the scope token.</summary>
    public const string TokenArgument = "EFCore.SchemaSync.ScopeToken";

    /// <inheritdoc />
    protected override void OnExecute(DeploymentPlanContributorContext context)
    {
        if (!context.Arguments.TryGetValue(TokenArgument, out var token) || DeploymentScope.Find(token) is not { } scope)
        {
            return;
        }

        var removed = new List<string>();
        for (var step = context.PlanHandle.Head; step is not null;)
        {
            var next = step.Next;
            if (step is DropElementStep { TargetElement: { } element } && scope.IsOutOfScope(SchemaOf(element), OwningTable(element)))
            {
                var name = element.Name.ToString();
                removed.Add(name);
                scope.RecordRemoved(name);
                Remove(context.PlanHandle, step);
            }

            step = next;
        }

        if (removed.Count == 0)
        {
            return;
        }

        for (var step = context.PlanHandle.Head; step is not null;)
        {
            var next = step.Next;
            if (step.GetType() == typeof(DeploymentScriptStep))
            {
                var sql = string.Join("\n", step.GenerateTSQL());
                if (removed.Any(name => sql.Contains($"select top 1 1 from {name})", StringComparison.OrdinalIgnoreCase)))
                {
                    Remove(context.PlanHandle, step);
                }
            }

            step = next;
        }
    }

    private static string? SchemaOf(TSqlObject element)
    {
        var parts = element.Name.Parts;
        if (parts.Count == 0)
        {
            return null;
        }

        return element.ObjectType == Schema.TypeClass || parts.Count > 1 ? parts[0] : null;
    }

    private static string? OwningTable(TSqlObject element)
    {
        for (var current = element; current is not null; current = current.GetParent())
        {
            if (current.ObjectType == Table.TypeClass && current.Name.Parts.Count == 2)
            {
                return DeploymentScope.Qualify(current.Name.Parts[0], current.Name.Parts[1]);
            }
        }

        return null;
    }
}
