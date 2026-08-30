using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;

namespace SampleApp;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> Items => Set<OrderItem>();
    public DbSet<ChangeLogEntry> ChangeLogEntries => Set<ChangeLogEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Items)
            .WithOne(i => i.Order!)
            .HasForeignKey(i => i.OrderId);

        // Global query filter: pairs with SoftDeleteSaveChangesInterceptor.
        modelBuilder.Entity<Order>().HasQueryFilter(o => !o.IsDeleted);

        // EF Core 10 named query filters: exclude soft-deleted rows everywhere.
        modelBuilder.Entity<OrderItem>()
            .HasQueryFilter("NotDeleted", i => !i.IsDeleted);
    }
}

/// <summary>Same schema, but guarded against writes — a reporting context.</summary>
public class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}
