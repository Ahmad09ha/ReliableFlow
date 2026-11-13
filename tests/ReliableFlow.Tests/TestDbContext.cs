using Microsoft.EntityFrameworkCore;
using ReliableFlow.EntityFrameworkCore;

namespace ReliableFlow.Tests;

public sealed class TestCounter
{
    public int Id { get; set; }
    public int Value { get; set; }
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<TestCounter> Counters => Set<TestCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestCounter>().HasKey(x => x.Id);
        modelBuilder.AddReliableFlow();
    }
}
