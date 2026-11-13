using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReliableFlow.EntityFrameworkCore;

namespace ReliableFlow.PostgresStress;

/// <summary>
/// Executable, provider-real concurrency checks. Only runs when an explicit
/// local PostgreSQL admin connection is provided. Every run creates its own
/// randomly named database, then drops only that same database.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var connectionString = Environment.GetEnvironmentVariable("RF_PG_ADMIN_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "Set RF_PG_ADMIN_CONNECTION to a disposable LOCAL PostgreSQL server (Database=postgres).");
            return 2;
        }

        var adminConfig = new NpgsqlConnectionStringBuilder(connectionString);
        if (adminConfig.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            adminConfig.Database != "postgres")
        {
            Console.Error.WriteLine(
                "Safety guard: this harness only accepts localhost with Database=postgres.");
            return 2;
        }

        var databaseName = "rfstress_" + Guid.NewGuid().ToString("N")[..16];
        await using var admin = new NpgsqlConnection(adminConfig.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var runConfig = new NpgsqlConnectionStringBuilder(adminConfig.ConnectionString)
            {
                Database = databaseName,
                Pooling = false
            };
            var options = new DbContextOptionsBuilder<StressDbContext>()
                .UseNpgsql(runConfig.ConnectionString)
                .Options;

            await using (var setup = new StressDbContext(options))
                await setup.Database.EnsureCreatedAsync();

            await ConcurrentSameMessageId(options);
            await ConcurrentDistinctMessages(options);
            await ConcurrentOutboxClaims(options);
            await ExpiredLeaseIsReclaimable(options);
            Console.WriteLine("PASS: real PostgreSQL concurrency and lease stress suite");
            return 0;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            Console.WriteLine($"Cleaned up temporary database {databaseName}");
        }
    }

    private static async Task ConcurrentSameMessageId(
        DbContextOptions<StressDbContext> options)
    {
        await using (var seed = new StressDbContext(options))
        {
            seed.Counters.Add(new StressCounter { Id = 1, Value = 0 });
            await seed.SaveChangesAsync();
        }

        var sharedMessageId = Guid.NewGuid();
        var inbox = new InboxProcessor<StressDbContext>();
        var barrier = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 32).Select(async _ =>
        {
            await barrier.Task;
            await using var db = new StressDbContext(options);
            return await inbox.ProcessAsync(
                db, "postgres.same-message.v1", sharedMessageId,
                async (ctx, ct) =>
                {
                    var changed = await ctx.Counters
                        .Where(x => x.Id == 1)
                        .ExecuteUpdateAsync(
                            update => update.SetProperty(x => x.Value, x => x.Value + 1),
                            ct);
                    Require(changed == 1, "Counter row missing");
                    // Increase contention so the UNIQUE index must arbitrate.
                    await Task.Delay(50, ct);
                });
        }).ToArray();
        barrier.SetResult(true);
        var outcomes = await Task.WhenAll(tasks);

        Require(outcomes.Count(x => x == InboxResult.Processed) == 1,
            "Exactly one delivery must return Processed");
        Require(outcomes.Count(x => x == InboxResult.Duplicate) == 31,
            "All other concurrent deliveries must return Duplicate");

        await using var verify = new StressDbContext(options);
        Require((await verify.Counters.SingleAsync(x => x.Id == 1)).Value == 1,
            "Concurrent duplicate deliveries changed the business state twice");
        Require(await verify.Set<InboxMessage>().CountAsync() == 1,
            "Unexpected inbox marker count");
        Console.WriteLine("PASS: 32 concurrent identical messages -> 1 effect, 31 duplicates");
    }

    private static async Task ConcurrentDistinctMessages(
        DbContextOptions<StressDbContext> options)
    {
        var inbox = new InboxProcessor<StressDbContext>();
        var barrier = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 24).Select(async _ =>
        {
            var messageId = Guid.NewGuid();
            await barrier.Task;
            await using var db = new StressDbContext(options);
            return await inbox.ProcessAsync(db, "postgres.distinct-message.v1",
                messageId, async (ctx, ct) =>
                {
                    var changed = await ctx.Counters
                        .Where(x => x.Id == 1)
                        .ExecuteUpdateAsync(
                            update => update.SetProperty(x => x.Value, x => x.Value + 1),
                            ct);
                    Require(changed == 1, "Missing counter");
                });
        }).ToArray();
        barrier.SetResult(true);
        var results = await Task.WhenAll(tasks);

        Require(results.All(x => x == InboxResult.Processed),
            "Some unique messages were lost");
        await using var verify = new StressDbContext(options);
        Require((await verify.Counters.SingleAsync(x => x.Id == 1)).Value == 25,
            "Lost update while processing distinct messages concurrently");
        Console.WriteLine("PASS: 24 unique messages concurrently apply 24 atomic increments");
    }

    private static async Task ConcurrentOutboxClaims(
        DbContextOptions<StressDbContext> options)
    {
        await using (var seed = new StressDbContext(options))
        {
            seed.EnqueueOutbox("stress.event.v1", new { Value = 1 });
            await seed.SaveChangesAsync();
        }

        var transport = new CountingTransport();
        var config = Options.Create(new OutboxOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            BatchSize = 16
        });
        var barrier = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 16).Select(async _ =>
        {
            await barrier.Task;
            await using var db = new StressDbContext(options);
            var relay = new OutboxDispatcher<StressDbContext>(
                db, transport, config, NullLogger<OutboxDispatcher<StressDbContext>>.Instance);
            return await relay.DispatchBatchAsync();
        }).ToArray();
        barrier.SetResult(true);
        var publishedCounts = await Task.WhenAll(tasks);

        Require(publishedCounts.Sum() == 1, "Multiple relays claimed one leased row");
        Require(transport.Publishes == 1, "One row was sent multiple times without lease expiry");
        await using var verify = new StressDbContext(options);
        Require((await verify.Set<OutboxMessage>().SingleAsync()).PublishedAtUtc is not null,
            "Outbox row not marked after publication");
        Console.WriteLine("PASS: 16 competing dispatchers -> one active lease / one publish");
    }

    private static async Task ExpiredLeaseIsReclaimable(
        DbContextOptions<StressDbContext> options)
    {
        Guid messageId;
        await using (var seed = new StressDbContext(options))
        {
            messageId = seed.EnqueueOutbox("stress.expired-lease.v1", new { Value = 2 });
            await seed.SaveChangesAsync();
            await seed.Set<OutboxMessage>().Where(x => x.Id == messageId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.LeaseId, (Guid?)Guid.NewGuid())
                    .SetProperty(x => x.LeaseExpiresAtUtc,
                        (DateTime?)DateTime.UtcNow.AddMilliseconds(850)));
        }

        var transport = new CountingTransport();
        var config = Options.Create(new OutboxOptions { LeaseDuration = TimeSpan.FromSeconds(5) });
        await using (var db = new StressDbContext(options))
        {
            var dispatcher = new OutboxDispatcher<StressDbContext>(
                db, transport, config, NullLogger<OutboxDispatcher<StressDbContext>>.Instance);
            Require(await dispatcher.DispatchBatchAsync() == 0, "Unexpired lease was stolen");
            await Task.Delay(1150);
            Require(await dispatcher.DispatchBatchAsync() == 1, "Expired lease was not reclaimed");
        }

        Require(transport.Publishes == 1, "Expected exactly one publish after lease expiry");
        Console.WriteLine("PASS: previously held lease becomes reclaimable after expiry");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StressDbContext(DbContextOptions<StressDbContext> options)
        : DbContext(options)
    {
        public DbSet<StressCounter> Counters => Set<StressCounter>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StressCounter>(b =>
            {
                b.ToTable("stress_counters");
                b.HasKey(x => x.Id);
            });
            modelBuilder.AddReliableFlow();
        }
    }

    private sealed class StressCounter
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }

    private sealed class CountingTransport : IOutboxTransport
    {
        private int _publishes;
        public int Publishes => Volatile.Read(ref _publishes);

        public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _publishes);
            await Task.Delay(200, cancellationToken);
        }
    }
}
