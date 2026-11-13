using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ReliableFlow.EntityFrameworkCore;
using Shared.Contracts;

namespace Inventory.Service;

/// <summary>Manual ACK only after the inbox + business transaction commits.</summary>
public sealed class OrderPlacedConsumer(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<OrderPlacedConsumer> logger,
    DemoConsumerCounters counters) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RabbitMQ consumer disconnected. Reconnecting.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMQ:Host"] ?? "localhost",
            VirtualHost = configuration["RabbitMQ:VirtualHost"] ?? "/",
            UserName = configuration["RabbitMQ:User"] ?? "guest",
            Password = configuration["RabbitMQ:Password"] ?? "guest",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(EventNames.OrderPlacedDeadLetter,
            durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(EventNames.OrderPlacedV1,
            durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = 10,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = EventNames.OrderPlacedDeadLetter
            }, cancellationToken: cancellationToken);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 8,
            global: false, cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            // Body memory belongs to RabbitMQ and must be consumed/copied within callback.
            var body = delivery.Body.ToArray();
            try
            {
                var messageIdText = delivery.BasicProperties.MessageId;
                if (!Guid.TryParse(messageIdText, out var messageId) || messageId == Guid.Empty ||
                    delivery.BasicProperties.Type != EventNames.OrderPlacedV1)
                    throw new JsonException("Missing/invalid event type or message ID");

                var payload = JsonSerializer.Deserialize<OrderPlacedV1>(body)
                    ?? throw new JsonException("Empty event body");
                if (payload.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Sku) ||
                    payload.Quantity <= 0)
                    throw new JsonException("Invalid event payload");

                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
                var inbox = scope.ServiceProvider.GetRequiredService<InboxProcessor<InventoryDbContext>>();

                var result = await inbox.ProcessAsync(
                    db,
                    consumerName: "inventory.order-reservation.v1",
                    messageId: messageId,
                    handle: async (context, ct) =>
                    {
                        // A second event with a DIFFERENT MessageId can still
                        // represent the same OrderId. Check the business key too.
                        var existing = await context.Reservations.AsNoTracking()
                            .SingleOrDefaultAsync(x => x.OrderId == payload.OrderId, ct);
                        if (existing is not null)
                        {
                            if (existing.Sku != payload.Sku || existing.Quantity != payload.Quantity)
                                throw new InvalidOperationException(
                                    "Conflicting event payload for an already processed order");
                            return;
                        }

                        // Atomic UPDATE avoids lost updates if distinct orders for
                        // the same SKU are processed concurrently.
                        var updated = await context.Items
                            .Where(x => x.Sku == payload.Sku && x.Available >= payload.Quantity)
                            .ExecuteUpdateAsync(s => s.SetProperty(
                                x => x.Available, x => x.Available - payload.Quantity), ct);

                        context.Reservations.Add(new Reservation
                        {
                            OrderId = payload.OrderId,
                            Sku = payload.Sku,
                            Quantity = payload.Quantity,
                            Status = updated == 1 ? "Reserved" : "Rejected",
                            CreatedAtUtc = DateTime.UtcNow
                        });
                        // The unique OrderId reservation key also guards against
                        // different MessageIds for the same order (a business-key issue).
                    }, cancellationToken);

                logger.LogInformation("Event {MessageId}: {Result} for order {OrderId}",
                    messageId, result, payload.OrderId);
                counters.Record(result);
                await channel.BasicAckAsync(delivery.DeliveryTag,
                    multiple: false, cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Poison event {DeliveryTag}; sending to DLQ", delivery.DeliveryTag);
                await channel.BasicRejectAsync(delivery.DeliveryTag,
                    requeue: false, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Event {DeliveryTag} failed; RabbitMQ will retry", delivery.DeliveryTag);
                // Quorum queue delivery-limit sends persistently failing deliveries to DLQ.
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                await channel.BasicNackAsync(delivery.DeliveryTag,
                    multiple: false, requeue: true, cancellationToken: cancellationToken);
            }
        };

        await channel.BasicConsumeAsync(queue: EventNames.OrderPlacedV1, autoAck: false,
            consumer: consumer, cancellationToken: cancellationToken);
        logger.LogInformation("Consuming {Queue} with manual acknowledgements", EventNames.OrderPlacedV1);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
