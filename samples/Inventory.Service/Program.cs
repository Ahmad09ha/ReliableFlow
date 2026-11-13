using Inventory.Service;
using Microsoft.EntityFrameworkCore;
using ReliableFlow.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Inventory")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Inventory")));
builder.Services.AddReliableInbox<InventoryDbContext>();
builder.Services.AddSingleton<DemoConsumerCounters>();
builder.Services.AddHostedService<OrderPlacedConsumer>();

var app = builder.Build();

// Self-contained demo bootstrap; use schema migrations in production.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (!await db.Items.AnyAsync())
    {
        db.Items.AddRange(
            new InventoryItem { Sku = "BOOK-001", Available = 100 },
            new InventoryItem { Sku = "LAPTOP-001", Available = 25 },
            new InventoryItem { Sku = "HEADPHONES-001", Available = 50 });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "inventory" }));
app.MapGet("/stock", async (InventoryDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Items.AsNoTracking().OrderBy(x => x.Sku).ToListAsync(ct)));
app.MapGet("/reservations", async (InventoryDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Reservations.AsNoTracking()
        .OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync(ct)));
app.MapGet("/inbox", async (InventoryDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Set<InboxMessage>().AsNoTracking()
        .OrderByDescending(x => x.ProcessedAtUtc).Take(100).ToListAsync(ct)));

if (app.Environment.IsDevelopment())
    app.MapGet("/demo/consumer-metrics", (DemoConsumerCounters counters) =>
        Results.Ok(new { processed = counters.Processed, duplicates = counters.Duplicates }));

app.Run();
