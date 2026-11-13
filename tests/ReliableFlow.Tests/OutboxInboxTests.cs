using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReliableFlow.EntityFrameworkCore;
using Xunit;

namespace ReliableFlow.Tests;

public sealed class OutboxInboxTests
{
    [Fact]
    public void Invalid_outbox_options_are_rejected()
    {
        var services = new ServiceCollection();
        services.AddReliableOutbox<TestDbContext>(options =>
        {
            options.LeaseDuration = TimeSpan.Zero;
        });
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<OutboxOptions>>().Value);
    }

    [Fact]
    public void Producer_rejects_empty_message_id()
    {
        var (connection, options) = OpenDatabase();
        using var owner = connection;
        using var db = new TestDbContext(options);
        Assert.Throws<ArgumentException>(
            () => db.EnqueueOutbox("counter.changed.v1", new { Value = 1 }, Guid.Empty));
    }

    private static (SqliteConnection connection, DbContextOptions<TestDbContext> options) OpenDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection).Options;
        using var db = new TestDbContext(options);
        db.Database.EnsureCreated();
        return (connection, options);
    }

    [Fact]
    public async Task Business_change_and_outbox_are_committed_together()
    {
        var (connection, options) = OpenDatabase();
        await using var owner = connection;
        await using (var db = new TestDbContext(options))
        {
            db.Counters.Add(new TestCounter { Id = 1, Value = 5 });
            db.EnqueueOutbox("counter.changed.v1", new { CounterId = 1, Value = 5 });
            await db.SaveChangesAsync();
        }
        await using var verify = new TestDbContext(options);
        Assert.Single(await verify.Counters.ToListAsync());
        Assert.Single(await verify.Set<OutboxMessage>().ToListAsync());
    }

    [Fact]
    public async Task Rollback_removes_both_business_change_and_event()
    {
        var (connection, options) = OpenDatabase();
        await using var owner = connection;
        await using (var db = new TestDbContext(options))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Counters.Add(new TestCounter { Id = 1, Value = 5 });
            db.EnqueueOutbox("counter.changed.v1", new { CounterId = 1 });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        await using var verify = new TestDbContext(options);
        Assert.Empty(await verify.Counters.ToListAsync());
        Assert.Empty(await verify.Set<OutboxMessage>().ToListAsync());
    }

    [Fact]
    public async Task Duplicate_event_has_one_business_effect()
    {
        var (connection, options) = OpenDatabase();
        await using var owner = connection;
        var id = Guid.NewGuid();
        var processor = new InboxProcessor<TestDbContext>();

        await using (var db = new TestDbContext(options))
        {
            db.Counters.Add(new TestCounter { Id = 1, Value = 0 });
            await db.SaveChangesAsync();
        }

        async Task<InboxResult> Consume()
        {
            await using var db = new TestDbContext(options);
            return await processor.ProcessAsync(db, "counter.consumer.v1", id,
                async (context, ct) =>
                {
                    var counter = await context.Counters.SingleAsync(x => x.Id == 1, ct);
                    counter.Value++;
                });
        }

        Assert.Equal(InboxResult.Processed, await Consume());
        Assert.Equal(InboxResult.Duplicate, await Consume());

        await using var verify = new TestDbContext(options);
        Assert.Equal(1, (await verify.Counters.SingleAsync()).Value);
        Assert.Single(await verify.Set<InboxMessage>().ToListAsync());
    }

    [Fact]
    public async Task Failed_business_handler_rolls_back_marker_and_can_retry()
    {
        var (connection, options) = OpenDatabase();
        await using var owner = connection;
        var id = Guid.NewGuid();
        var processor = new InboxProcessor<TestDbContext>();

        await using (var db = new TestDbContext(options))
        {
            db.Counters.Add(new TestCounter { Id = 1 });
            await db.SaveChangesAsync();
        }

        await using (var db = new TestDbContext(options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(
                db, "counter.consumer.v1", id, (context, ct) =>
                {
                    context.Counters.Single(x => x.Id == 1).Value = 99;
                    throw new InvalidOperationException("simulate failure");
                }));
        }

        await using (var verify = new TestDbContext(options))
        {
            Assert.Empty(await verify.Set<InboxMessage>().ToListAsync());
            Assert.Equal(0, (await verify.Counters.SingleAsync()).Value);
        }

        await using (var retry = new TestDbContext(options))
        {
            var result = await processor.ProcessAsync(retry, "counter.consumer.v1", id,
                (context, ct) =>
                {
                    context.Counters.Single(x => x.Id == 1).Value = 1;
                    return Task.CompletedTask;
                });
            Assert.Equal(InboxResult.Processed, result);
        }
    }

    [Fact]
    public async Task Same_message_id_can_be_processed_by_different_consumers()
    {
        var (connection, options) = OpenDatabase();
        await using var owner = connection;
        var id = Guid.NewGuid();
        var processor = new InboxProcessor<TestDbContext>();
        await using (var first = new TestDbContext(options))
            Assert.Equal(InboxResult.Processed, await processor.ProcessAsync(first,
                "email.notifications", id, (_, _) => Task.CompletedTask));
        await using (var second = new TestDbContext(options))
            Assert.Equal(InboxResult.Processed, await processor.ProcessAsync(second,
                "inventory.reservation", id, (_, _) => Task.CompletedTask));
        await using var verify = new TestDbContext(options);
        Assert.Equal(2, await verify.Set<InboxMessage>().CountAsync());
    }

    [Fact]
    public async Task Dispatcher_retries_after_transport_failure()
    {
        var (connection, options) = OpenDatabase();
        await using var owner = connection;
        await using (var seed = new TestDbContext(options))
        {
            seed.EnqueueOutbox("counter.changed.v1", new { Id = 1 });
            await seed.SaveChangesAsync();
        }

        var transport = new FailsOnceTransport();
        var optionsObject = Options.Create(new OutboxOptions
        {
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(1)
        });
        await using (var db = new TestDbContext(options))
        {
            var dispatcher = new OutboxDispatcher<TestDbContext>(db, transport,
                optionsObject, NullLogger<OutboxDispatcher<TestDbContext>>.Instance);
            Assert.Equal(0, await dispatcher.DispatchBatchAsync());
            Assert.Equal(1, (await db.Set<OutboxMessage>().SingleAsync()).AttemptCount);
            await Task.Delay(20);
            Assert.Equal(1, await dispatcher.DispatchBatchAsync());
        }

        await using var verify = new TestDbContext(options);
        Assert.NotNull((await verify.Set<OutboxMessage>().SingleAsync()).PublishedAtUtc);
        Assert.Equal(2, transport.Attempts);
    }

    [Fact]
    public async Task Post_publish_failure_replays_message_but_inbox_applies_effect_once()
    {
        var (connection, options) = OpenDatabase();
        await using var owner = connection;
        await using (var seed = new TestDbContext(options))
        {
            seed.Counters.Add(new TestCounter { Id = 42, Value = 0 });
            seed.EnqueueOutbox("counter.incremented.v1", new { CounterId = 42 });
            await seed.SaveChangesAsync();
        }

        var transport = new ConfirmThenFailOnceTransport();
        var config = Options.Create(new OutboxOptions
        {
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(1)
        });
        await using (var publisher = new TestDbContext(options))
        {
            var relay = new OutboxDispatcher<TestDbContext>(publisher, transport,
                config, NullLogger<OutboxDispatcher<TestDbContext>>.Instance);
            Assert.Equal(0, await relay.DispatchBatchAsync());
            await Task.Delay(25);
            Assert.Equal(1, await relay.DispatchBatchAsync());
        }

        Assert.Equal(2, transport.Delivered.Count);
        Assert.Equal(transport.Delivered[0], transport.Delivered[1]);

        var inbox = new InboxProcessor<TestDbContext>();
        var outcomes = new List<InboxResult>();
        foreach (var messageId in transport.Delivered)
        {
            await using var consumer = new TestDbContext(options);
            outcomes.Add(await inbox.ProcessAsync(consumer, "inventory.consumer.v1",
                messageId, async (db, ct) =>
                {
                    var counter = await db.Counters.SingleAsync(x => x.Id == 42, ct);
                    counter.Value++;
                }));
        }

        Assert.Equal(new[] { InboxResult.Processed, InboxResult.Duplicate }, outcomes);
        await using var verify = new TestDbContext(options);
        Assert.Equal(1, (await verify.Counters.SingleAsync(x => x.Id == 42)).Value);
    }

    private sealed class ConfirmThenFailOnceTransport : IOutboxTransport
    {
        public List<Guid> Delivered { get; } = [];

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Delivered.Add(message.Id);
            if (Delivered.Count == 1)
                throw new InvalidOperationException("Simulated crash after confirmed publish");
            return Task.CompletedTask;
        }
    }

    private sealed class FailsOnceTransport : IOutboxTransport
    {
        public int Attempts { get; private set; }
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1) throw new InvalidOperationException("simulated broker outage");
            return Task.CompletedTask;
        }
    }
}
