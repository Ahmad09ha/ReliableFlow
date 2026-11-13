using Microsoft.EntityFrameworkCore;
using ReliableFlow.EntityFrameworkCore;

namespace Inventory.Service;

public sealed class InventoryItem
{
    public string Sku { get; set; } = string.Empty;
    public int Available { get; set; }
}

public sealed class Reservation
{
    public Guid OrderId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<InventoryItem> Items => Set<InventoryItem>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryItem>(b =>
        {
            b.ToTable("inventory_items");
            b.HasKey(x => x.Sku);
            b.Property(x => x.Sku).HasMaxLength(60);
        });
        modelBuilder.Entity<Reservation>(b =>
        {
            b.ToTable("reservations");
            b.HasKey(x => x.OrderId); // additional business-key deduplication
            b.Property(x => x.Sku).HasMaxLength(60);
            b.Property(x => x.Status).HasMaxLength(20);
        });
        modelBuilder.AddReliableFlow();
    }
}
