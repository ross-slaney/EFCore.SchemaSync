using EFCore.SchemaSync;
using Microsoft.EntityFrameworkCore;
using SchemaSync.Sample;

// Our own flags are stripped so the host's command-line configuration provider never sees them.
var hostArgs = args.Where(a => !SchemaCommand.IsFlag(a)).ToArray();
var builder = WebApplication.CreateBuilder(hostArgs);

var connectionString = builder.Configuration.GetConnectionString("sampledb")
    ?? throw new InvalidOperationException("Connection string 'sampledb' is not configured. Run the app through SchemaSync.Sample.AppHost or set ConnectionStrings__sampledb.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddHealthChecks();

var app = builder.Build();

if (SchemaCommand.IsRequested(args))
{
    // `dotnet SchemaSync.Sample.dll --apply-schema`: apply and exit, never start the web server.
    return await SchemaCommand.RunAsync(app.Services, args, app.Lifetime.ApplicationStopping);
}

// Normal startup: the EF model is the schema. Failure throws and prevents the host from starting.
await app.Services.ApplyDatabaseSchemaAsync<AppDbContext>();

app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Redirect("/lists"));

app.MapGet("/lists", async (AppDbContext db) =>
    await db.Lists.OrderBy(l => l.Id).Select(l => new { l.Id, l.Name, l.CreatedAt, ItemCount = l.Items.Count }).ToListAsync());

app.MapPost("/lists", async (AppDbContext db, CreateList request) =>
{
    var list = new TodoList { Name = request.Name };
    db.Lists.Add(list);
    await db.SaveChangesAsync();
    return Results.Created($"/lists/{list.Id}", new { list.Id, list.Name, list.CreatedAt });
});

app.MapGet("/lists/{listId:int}/items", async (AppDbContext db, int listId) =>
    await db.Items.Where(i => i.ListId == listId).OrderBy(i => i.Id)
        .Select(i => new { i.Id, i.Title, i.Notes, i.Priority, i.IsDone, i.DueDate }).ToListAsync());

app.MapPost("/lists/{listId:int}/items", async (AppDbContext db, int listId, CreateItem request) =>
{
    if (!await db.Lists.AnyAsync(l => l.Id == listId))
    {
        return Results.NotFound();
    }

    var item = new TodoItem { ListId = listId, Title = request.Title, Notes = request.Notes, DueDate = request.DueDate };
    if (request.Priority is { } priority)
    {
        item.Priority = priority;
    }

    db.Items.Add(item);
    await db.SaveChangesAsync();
    return Results.Created($"/lists/{listId}/items/{item.Id}", new { item.Id, item.Title, item.Notes, item.Priority, item.IsDone, item.DueDate });
});

app.MapPost("/lists/{listId:int}/items/{itemId:int}/done", async (AppDbContext db, int listId, int itemId) =>
{
    var item = await db.Items.SingleOrDefaultAsync(i => i.ListId == listId && i.Id == itemId);
    if (item is null)
    {
        return Results.NotFound();
    }

    item.IsDone = true;
    await db.SaveChangesAsync();
    return Results.Ok(new { item.Id, item.IsDone });
});

await app.RunAsync();
return 0;

public sealed record CreateList(string Name);

public sealed record CreateItem(string Title, string? Notes, int? Priority, DateOnly? DueDate);
