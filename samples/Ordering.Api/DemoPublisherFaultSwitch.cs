using System.Threading;

namespace Ordering.Api;

/// <summary>Only used by development-only demonstration routes.</summary>
public sealed class DemoPublisherFaultSwitch
{
    private int _available = 1;
    private int _failOnceAfterConfirmedPublish;
    private int _pauseOnceAfterConfirmedPublish;
    private int _paused;
    private TaskCompletionSource<bool>? _pauseGate;

    public bool Available => Volatile.Read(ref _available) == 1;
    public void SetAvailable(bool available) => Interlocked.Exchange(ref _available, available ? 1 : 0);
    public void ArmPostPublishFailure() => Interlocked.Exchange(ref _failOnceAfterConfirmedPublish, 1);
    public bool ConsumePostPublishFailure() => Interlocked.Exchange(ref _failOnceAfterConfirmedPublish, 0) == 1;

    /// <summary>
    /// A deterministic hard-crash hook: the broker has already confirmed the
    /// publish, but the relay has not updated the outbox row. Only for dev.
    /// The E2E harness observes IsPaused, then SIGKILLs the container.
    /// </summary>
    public void ArmPauseAfterConfirmedPublish()
    {
        Volatile.Write(ref _pauseGate,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        Interlocked.Exchange(ref _pauseOnceAfterConfirmedPublish, 1);
    }

    public bool IsPaused => Volatile.Read(ref _paused) == 1;

    public async Task PauseAfterConfirmedPublishIfArmedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _pauseOnceAfterConfirmedPublish, 0) != 1)
            return;
        var gate = Volatile.Read(ref _pauseGate)
            ?? throw new InvalidOperationException("Missing demo pause gate");
        Volatile.Write(ref _paused, 1);
        try { await gate.Task.WaitAsync(cancellationToken); }
        finally { Volatile.Write(ref _paused, 0); }
    }

    public void ResumePausedPublish() => Volatile.Read(ref _pauseGate)?.TrySetResult(true);
}
