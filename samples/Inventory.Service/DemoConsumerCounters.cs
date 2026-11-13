namespace Inventory.Service;

/// <summary>
/// Process-local counters used only by the demonstration's development routes.
/// They are not durable metrics or a replacement for observability.
/// </summary>
public sealed class DemoConsumerCounters
{
    private long _processed;
    private long _duplicates;

    public long Processed => Interlocked.Read(ref _processed);
    public long Duplicates => Interlocked.Read(ref _duplicates);

    public void Record(ReliableFlow.EntityFrameworkCore.InboxResult outcome)
    {
        if (outcome == ReliableFlow.EntityFrameworkCore.InboxResult.Processed)
            Interlocked.Increment(ref _processed);
        else
            Interlocked.Increment(ref _duplicates);
    }
}
