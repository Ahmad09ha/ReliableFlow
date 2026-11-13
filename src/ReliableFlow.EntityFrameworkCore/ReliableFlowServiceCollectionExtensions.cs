using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ReliableFlow.EntityFrameworkCore;

/// <summary>Dependency injection registration helpers for the optional outbox relay and inbox.</summary>
public static class ReliableFlowServiceCollectionExtensions
{
    /// <summary>
    /// Register on the producer host. The host must register its DbContext and
    /// an implementation of IOutboxTransport separately.
    /// </summary>
    public static IServiceCollection AddReliableOutbox<TDbContext>(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null) where TDbContext : DbContext
    {
        services.AddOptions<OutboxOptions>()
            .Validate(options => options.BatchSize > 0, "BatchSize must be positive.")
            .Validate(options => options.PollInterval > TimeSpan.Zero,
                "PollInterval must be positive.")
            .Validate(options => options.LeaseDuration > TimeSpan.Zero,
                "LeaseDuration must be positive.")
            .Validate(options => options.BaseRetryDelay > TimeSpan.Zero,
                "BaseRetryDelay must be positive.")
            .Validate(options => options.MaxRetryDelay >= options.BaseRetryDelay,
                "MaxRetryDelay must be at least BaseRetryDelay.")
            .ValidateOnStart();
        if (configure is not null) services.Configure(configure);
        services.AddScoped<OutboxDispatcher<TDbContext>>();
        services.AddHostedService<OutboxBackgroundService<TDbContext>>();
        return services;
    }

    /// <summary>Register the idempotent inbox processor for a receiving EF Core context.</summary>
    public static IServiceCollection AddReliableInbox<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddScoped<InboxProcessor<TDbContext>>();
        return services;
    }
}
