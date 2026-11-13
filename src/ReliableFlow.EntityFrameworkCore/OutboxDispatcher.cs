using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReliableFlow.EntityFrameworkCore;

/// <summary>
/// Database-leased, multi-instance-safe polling relay. Delivery is at least once:
/// the lease can expire or the process can die AFTER publish and BEFORE marking sent.
/// </summary>
public sealed class OutboxDispatcher<TDbContext>(
    TDbContext db,
    IOutboxTransport transport,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher<TDbContext>> logger)
    where TDbContext : DbContext
{
    /// <summary>Atomically lease candidate messages, publish and mark each confirmed send.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        var config = options.Value;
        var now = DateTime.UtcNow;

        // This initial query is just a candidate scan, NOT a lock. Each row is
        // claimed with an atomic conditional UPDATE below.
        var ids = await db.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(x => x.PublishedAtUtc == null && x.NextAttemptAtUtc <= now &&
                (x.LeaseExpiresAtUtc == null || x.LeaseExpiresAtUtc <= now))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => x.Id)
            .Take(config.BatchSize)
            .ToListAsync(cancellationToken);

        var published = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimTime = DateTime.UtcNow;
            var leaseId = Guid.NewGuid();
            var claimed = await db.Set<OutboxMessage>()
                .Where(x => x.Id == id && x.PublishedAtUtc == null &&
                    x.NextAttemptAtUtc <= claimTime &&
                    (x.LeaseExpiresAtUtc == null || x.LeaseExpiresAtUtc <= claimTime))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.LeaseId, (Guid?)leaseId)
                    .SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)claimTime.Add(config.LeaseDuration)),
                    cancellationToken);

            if (claimed != 1) continue;
            var message = await db.Set<OutboxMessage>()
                .AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);

            try
            {
                await transport.PublishAsync(message, cancellationToken);

                // Lease-token check prevents the old owner from overwriting a
                // newer claim. Expired leases can still produce duplicate sends.
                var updated = await db.Set<OutboxMessage>()
                    .Where(x => x.Id == id && x.LeaseId == leaseId && x.PublishedAtUtc == null)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.PublishedAtUtc, (DateTime?)DateTime.UtcNow)
                        .SetProperty(x => x.LeaseId, (Guid?)null)
                        .SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(x => x.LastError, (string?)null), cancellationToken);
                published += updated;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = message.AttemptCount + 1;
                var delaySeconds = Math.Min(config.MaxRetryDelay.TotalSeconds,
                    config.BaseRetryDelay.TotalSeconds * Math.Pow(2, Math.Min(attempts - 1, 12)));
                var nextAttempt = DateTime.UtcNow.AddSeconds(delaySeconds);
                await db.Set<OutboxMessage>()
                    .Where(x => x.Id == id && x.LeaseId == leaseId && x.PublishedAtUtc == null)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.AttemptCount, attempts)
                        .SetProperty(x => x.NextAttemptAtUtc, nextAttempt)
                        .SetProperty(x => x.LeaseId, (Guid?)null)
                        .SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(x => x.LastError, ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message),
                        cancellationToken);
                logger.LogWarning(ex, "Outbox publish failed for {MessageId} attempt {Attempt}", id, attempts);
            }
        }
        return published;
    }
}
