namespace ReliableFlow.EntityFrameworkCore;

/// <summary>A durable intent to publish a single integration event.</summary>
public sealed class OutboxMessage
{
    /// <summary>Stable event identifier, reused as the broker MessageId on every retry.</summary>
    public Guid Id { get; set; }
    /// <summary>Versioned event contract / routing key.</summary>
    public string EventType { get; set; } = string.Empty;
    /// <summary>JSON-serialized event payload.</summary>
    public string Payload { get; set; } = string.Empty;
    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Earliest time another publish attempt may start.</summary>
    public DateTime NextAttemptAtUtc { get; set; }
    /// <summary>UTC time of a confirmed broker publish, if known.</summary>
    public DateTime? PublishedAtUtc { get; set; }
    /// <summary>Number of failed attempts observed by the relay.</summary>
    public int AttemptCount { get; set; }
    /// <summary>Current publishing lease owner, or null if unleased.</summary>
    public Guid? LeaseId { get; set; }
    /// <summary>UTC expiration of the publishing lease, or null if unleased.</summary>
    public DateTime? LeaseExpiresAtUtc { get; set; }
    /// <summary>Last recorded publishing error, truncated by the dispatcher.</summary>
    public string? LastError { get; set; }
}
