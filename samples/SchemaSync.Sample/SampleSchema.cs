using EFCore.SchemaSync;
using Microsoft.EntityFrameworkCore;

namespace SchemaSync.Sample;

/// <summary>Schema options shared by web startup and <c>--apply-schema</c>, so both deploy exactly the same way.</summary>
public static class SampleSchema
{
    public const string InboxName = "Inbox";

    public static void Configure(SchemaSyncOptions options)
        => options.AfterDeploy("seed inbox list", SeedInboxAsync);

    // Reference data in C# instead of a post-deployment SQL script. Runs under the schema lock after every
    // deployment (also when nothing changed), so it is written to be idempotent.
    private static async Task SeedInboxAsync(SchemaSyncHookContext hook, CancellationToken cancellationToken)
    {
        var db = hook.GetDbContext<AppDbContext>();
        if (!await db.Lists.AnyAsync(l => l.Name == InboxName, cancellationToken))
        {
            db.Lists.Add(new TodoList { Name = InboxName });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
