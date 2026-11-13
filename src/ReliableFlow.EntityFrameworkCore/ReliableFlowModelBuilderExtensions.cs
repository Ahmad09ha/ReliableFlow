using Microsoft.EntityFrameworkCore;

namespace ReliableFlow.EntityFrameworkCore;

/// <summary>Adds persistent outbox and inbox tables to a host EF Core model.</summary>
public static class ReliableFlowModelBuilderExtensions
{
    /// <summary>Call once from OnModelCreating in each DbContext using an outbox or inbox.</summary>
    public static ModelBuilder AddReliableFlow(this ModelBuilder modelBuilder, string? schema = null)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("reliable_outbox", schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            b.Property(x => x.Payload).IsRequired();
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.HasIndex(x => new { x.PublishedAtUtc, x.NextAttemptAtUtc, x.LeaseExpiresAtUtc });
        });

        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable("reliable_inbox", schema);
            b.HasKey(x => new { x.ConsumerName, x.MessageId });
            b.Property(x => x.ConsumerName).HasMaxLength(200).IsRequired();
            b.HasIndex(x => x.ProcessedAtUtc);
        });

        return modelBuilder;
    }
}
