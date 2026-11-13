using Microsoft.EntityFrameworkCore;
using ReliableFlow.EntityFrameworkCore;

namespace Ordering.Api;

public sealed class Order
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        });
        modelBuilder.AddReliableFlow();
    }
}
