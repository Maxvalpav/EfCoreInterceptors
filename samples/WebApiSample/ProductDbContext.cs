using EfCore.Interceptors;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApiSample;

public class ProductDbContext(
    DbContextOptions<ProductDbContext> options,
    ITenantProvider tenants) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Global filters composed with the interceptors' write-side behavior.
        modelBuilder.ApplySoftDeleteFilters();
        modelBuilder.ApplyTenantFilters(tenants);
    }
}
