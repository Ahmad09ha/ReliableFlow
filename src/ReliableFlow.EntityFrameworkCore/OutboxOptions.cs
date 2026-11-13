namespace ReliableFlow.EntityFrameworkCore;

/// <summary>Configuration for the background outbox polling relay.</summary>
public sealed class OutboxOptions
{
    /// <summary>Maximum candidate messages inspected per polling iteration.</summary>
    public int BatchSize { get; set; } = 20;
    /// <summary>Delay between polling iterations.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How long a relay owns a claimed message before another may retry it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>Initial backoff after a publish failure.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Maximum backoff after repeated publish failures.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
}
