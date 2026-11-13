namespace ReliableFlow.EntityFrameworkCore;

/// <summary>
/// Implement using your broker of choice. Return ONLY after a durable broker-level
/// acceptance (e.g., RabbitMQ publisher confirmation), not merely after writing to a socket.
/// A successful return is NOT an exactly-once delivery guarantee.
/// </summary>
public interface IOutboxTransport
{
    /// <summary>Publish the message, returning only after durable broker acceptance.</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}
