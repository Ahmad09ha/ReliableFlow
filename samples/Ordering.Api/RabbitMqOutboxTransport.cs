using System.Text;
using RabbitMQ.Client;
using ReliableFlow.EntityFrameworkCore;
using Shared.Contracts;

namespace Ordering.Api;

/// <summary>
/// Demo adapter. The reusable library only knows IOutboxTransport.
/// Creates a connection per publish for simplicity (not high-throughput production design).
/// </summary>
public sealed class RabbitMqOutboxTransport(
    IConfiguration configuration,
    DemoPublisherFaultSwitch faults) : IOutboxTransport
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!faults.Available)
            throw new InvalidOperationException("Demo fault: RabbitMQ publisher intentionally unavailable");

        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMQ:Host"] ?? "localhost",
            VirtualHost = configuration["RabbitMQ:VirtualHost"] ?? "/",
            UserName = configuration["RabbitMQ:User"] ?? "guest",
            Password = configuration["RabbitMQ:Password"] ?? "guest",
            AutomaticRecoveryEnabled = false
        };

        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(channelOptions, cancellationToken);

        // This demo has one event type. The sample queue name is its routing key.
        // Real deployments should establish topology separately (or adapt the transport).
        if (message.EventType != EventNames.OrderPlacedV1)
            throw new InvalidOperationException($"No demo route configured for {message.EventType}");

        await channel.QueueDeclareAsync(
            queue: EventNames.OrderPlacedDeadLetter, durable: true, exclusive: false,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: EventNames.OrderPlacedV1, durable: true, exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = 10,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = EventNames.OrderPlacedDeadLetter
            }, cancellationToken: cancellationToken);

        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString("D"),
            Type = message.EventType,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent
        };

        // With publisher confirmations enabled, awaiting this call waits for broker confirm.
        // There is a known route because the target queue was declared immediately above.
        await channel.BasicPublishAsync(
            exchange: string.Empty, routingKey: message.EventType,
            mandatory: true, basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.Payload),
            cancellationToken: cancellationToken);

        await faults.PauseAfterConfirmedPublishIfArmedAsync(cancellationToken);

        // Simulates a crash *after* confirmation but *before* the outbox marker update.
        // No process is killed; a controlled exception recreates the same retry window.
        if (faults.ConsumePostPublishFailure())
            throw new InvalidOperationException("Demo fault: simulated process crash after broker confirm");
    }
}
