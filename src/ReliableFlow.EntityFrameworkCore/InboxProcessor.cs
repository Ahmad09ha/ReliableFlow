using Microsoft.EntityFrameworkCore;

namespace ReliableFlow.EntityFrameworkCore;

/// <summary>Result of attempting a consumer-scoped idempotent event handler.</summary>
public enum InboxResult
{
    /// <summary>Handler and inbox marker committed in one local transaction.</summary>
    Processed,
    /// <summary>A committed inbox marker already existed; handler was not invoked.</summary>
    Duplicate
}

/// <summary>
/// Consumer-specific idempotency. The unique inbox marker is inserted BEFORE
/// executing the business handler, but COMMITTED WITH the business handler.
/// All transactional business writes MUST use the provided DbContext.
/// </summary>
public sealed class InboxProcessor<TDbContext> where TDbContext : DbContext
{
    /// <summary>
    /// Run a handler in a new transaction, inserting a unique inbox marker first.
    /// The handler MUST make transactional changes through the supplied context.
    /// This operation owns its transaction and does not support an already-active one.
    /// </summary>
    public async Task<InboxResult> ProcessAsync(
        TDbContext db,
        string consumerName,
        Guid messageId,
        Func<TDbContext, CancellationToken, Task> handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        if (consumerName.Length > 200) throw new ArgumentOutOfRangeException(nameof(consumerName));
        if (messageId == Guid.Empty) throw new ArgumentException("Message ID cannot be empty", nameof(messageId));

        if (await db.Set<InboxMessage>().AsNoTracking().AnyAsync(x =>
                x.ConsumerName == consumerName && x.MessageId == messageId, cancellationToken))
            return InboxResult.Duplicate;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Set<InboxMessage>().Add(new InboxMessage
        {
            ConsumerName = consumerName,
            MessageId = messageId,
            ProcessedAtUtc = DateTime.UtcNow
        });

        try
        {
            // Forces the composite UNIQUE constraint to arbitrate concurrent deliveries.
            // The row remains uncommitted until the handler's writes commit too.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            // End the failed transaction before the post-rollback check: PostgreSQL
            // cannot execute another statement inside a failed transaction.
            await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            // Only classify an insert failure as duplicate when the marker EXISTS.
            if (await db.Set<InboxMessage>().AsNoTracking().AnyAsync(x =>
                    x.ConsumerName == consumerName && x.MessageId == messageId, CancellationToken.None))
                return InboxResult.Duplicate;
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        try
        {
            await handle(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InboxResult.Processed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
