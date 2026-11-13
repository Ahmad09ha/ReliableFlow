namespace ReliableFlow.EntityFrameworkCore;

/// <summary>A consumer-specific processed message marker. Unique (ConsumerName, MessageId).</summary>
public sealed class InboxMessage
{
    /// <summary>Logical consumer identity; different consumers can handle the same event.</summary>
    public string ConsumerName { get; set; } = string.Empty;
    /// <summary>Original stable event identifier from the broker.</summary>
    public Guid MessageId { get; set; }
    /// <summary>UTC time at which processing committed.</summary>
    public DateTime ProcessedAtUtc { get; set; }
}
