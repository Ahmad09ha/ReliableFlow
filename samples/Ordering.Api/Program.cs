using Microsoft.EntityFrameworkCore;
using Ordering.Api;
using ReliableFlow.EntityFrameworkCore;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Orders")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Orders")));
builder.Services.AddSingleton<DemoPublisherFaultSwitch>();
builder.Services.AddSingleton<IOutboxTransport, RabbitMqOutboxTransport>();
builder.Services.AddReliableOutbox<OrdersDbContext>(options =>
{
    options.PollInterval = TimeSpan.FromMilliseconds(700);
    options.LeaseDuration = TimeSpan.FromSeconds(40);
    // Optional local failure-lab speedup. Never shorten production leases
    // without considering worst-case broker confirmation latency.
    var labLeaseSeconds = builder.Configuration.GetValue<int>("ReliableFlow:DemoLeaseSeconds");
    if (builder.Environment.IsDevelopment() && labLeaseSeconds >= 5)
        options.LeaseDuration = TimeSpan.FromSeconds(labLeaseSeconds);
});

var app = builder.Build();

// For a self-contained demo only. Production consumers MUST use EF migrations.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ordering" }));

app.MapPost("/orders", async (CreateOrderRequest request, OrdersDbContext db, CancellationToken ct) =>
{
    var sku = request.Sku?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(sku) || sku.Length > 60 || request.Quantity is < 1 or > 100)
        return Results.BadRequest(new { error = "Sku must be 1-60 characters; Quantity must be 1-100." });

    var order = new Order
    {
        Id = Guid.NewGuid(),
        Sku = sku,
        Quantity = request.Quantity,
        CreatedAtUtc = DateTime.UtcNow
    };
    db.Orders.Add(order);

    // Both rows are inserted with ONE SaveChanges transaction.
    // There is deliberately no direct RabbitMQ call in this endpoint.
    var eventId = db.EnqueueOutbox(
        EventNames.OrderPlacedV1,
        new OrderPlacedV1(order.Id, order.Sku, order.Quantity));
    await db.SaveChangesAsync(ct);

    return Results.Created($"/orders/{order.Id}", new { order.Id, order.Sku, order.Quantity, EventId = eventId });
});

app.MapGet("/orders", async (OrdersDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Orders.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct)));

app.MapGet("/orders/{id:guid}", async (Guid id, OrdersDbContext db, CancellationToken ct) =>
{
    var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.MapGet("/outbox", async (OrdersDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Set<OutboxMessage>().AsNoTracking()
        .OrderByDescending(x => x.CreatedAtUtc)
        .Select(x => new { x.Id, x.EventType, x.CreatedAtUtc, x.PublishedAtUtc,
            x.AttemptCount, x.NextAttemptAtUtc, x.LastError })
        .Take(100).ToListAsync(ct)));

if (app.Environment.IsDevelopment())
{
    app.MapPost("/demo/faults/publisher/down", (DemoPublisherFaultSwitch faults) =>
    {
        faults.SetAvailable(false);
        return Results.Ok(new { publisherAvailable = false });
    });
    app.MapPost("/demo/faults/publisher/up", (DemoPublisherFaultSwitch faults) =>
    {
        faults.SetAvailable(true);
        return Results.Ok(new { publisherAvailable = true });
    });
    app.MapPost("/demo/faults/fail-after-publish-once", (DemoPublisherFaultSwitch faults) =>
    {
        faults.ArmPostPublishFailure();
        return Results.Ok(new { armed = true, meaning = "Next confirmed publish throws before outbox update" });
    });
    app.MapPost("/demo/faults/pause-after-publish-once", (DemoPublisherFaultSwitch faults) =>
    {
        faults.ArmPauseAfterConfirmedPublish();
        return Results.Ok(new { armed = true, meaning = "Next broker-confirmed publish pauses until released or process killed" });
    });
    app.MapGet("/demo/faults/status", (DemoPublisherFaultSwitch faults) =>
        Results.Ok(new { isPausedAfterConfirm = faults.IsPaused }));
    app.MapPost("/demo/faults/resume-publish", (DemoPublisherFaultSwitch faults) =>
    {
        faults.ResumePausedPublish();
        return Results.Ok(new { resumed = true });
    });
}

app.Run();

public sealed record CreateOrderRequest(string? Sku, int Quantity);
