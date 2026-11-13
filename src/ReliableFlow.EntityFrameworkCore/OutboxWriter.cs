using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ReliableFlow.EntityFrameworkCore;

/// <summary>Staging helpers for atomically storing domain changes and outgoing events.</summary>
public static class OutboxWriter
{
    /// <summary>
    /// Stages an event in THIS DbContext's change tracker. The caller MUST commit its
    /// business changes and this outbox row in the same SaveChanges / transaction.
    /// This method intentionally DOES NOT save or publish anything.
    /// </summary>
    public static Guid EnqueueOutbox<TEvent>(
        this DbContext db,
        string eventType,
        TEvent payload,
        Guid? messageId = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventType.Length > 200) throw new ArgumentOutOfRangeException(nameof(eventType));
        ArgumentNullException.ThrowIfNull(payload);

        var id = messageId ?? Guid.NewGuid();
        if (id == Guid.Empty)
            throw new ArgumentException("Message ID cannot be empty.", nameof(messageId));
        var now = DateTime.UtcNow;
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = id,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload, jsonOptions),
            CreatedAtUtc = now,
            NextAttemptAtUtc = now
        });
        return id;
    }
}
