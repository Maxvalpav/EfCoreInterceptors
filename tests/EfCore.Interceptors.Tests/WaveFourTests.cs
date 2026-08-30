using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors.Tests;

public class SqlLoggingRedactionTests
{
    [Fact]
    public void Redactor_masks_sensitive_fragments_in_logs()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        using var db = new SqliteTestDatabase().CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.WithSqlLogging(
                factory,
                includeParameterValues: true,
                textRedactor: sql => System.Text.RegularExpressions.Regex.Replace(
                    sql, @"\d{4}-\d{4}-\d{4}-\d{4}", "***CARD***")));
        });

        db.Database.EnsureCreated();
        db.VaultItems.Add(new VaultItem { Name = "Holder", CardNumber = "4111-1111-1111-1111" });
        db.SaveChanges();

        Assert.DoesNotContain(provider.Records.Select(r => r.Message), m => m.Contains("4111"));
    }
}

public class CacheAutoInvalidationTests
{
    [Fact]
    public void Writes_invalidate_the_cache_when_enabled()
    {
        using var database = new SqliteTestDatabase();
        var sharedCache = new CachingCommandInterceptor(TimeSpan.FromMinutes(5), invalidateOnWrites: true);

        using (var seed = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.Add(sharedCache))).Options))
        {
            seed.Database.EnsureCreated();
            seed.TenantPets.Add(new TenantPet { Name = "Before" });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.Add(sharedCache))).Options);

        // Prime the cache.
        Assert.Equal(["Before"], ctx.TenantPets.Select(p => p.Name).ToList());

        // A write (raw SQL here) clears the cache automatically...
        ctx.Database.ExecuteSqlRaw("UPDATE TenantPets SET Name = 'After'");

        // ...so the next read is fresh without manual invalidation.
        Assert.Equal(["After"], ctx.TenantPets.Select(p => p.Name).ToList());
    }
}

public class ValidationSaveChangesInterceptorTests
{
    private class Profile
    {
        public int Id { get; set; }

        [System.ComponentModel.DataAnnotations.Required]
        public string Login { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Range(0, 150)]
        public int Age { get; set; }
    }

    private class ValidationDbContext(DbContextOptions<ValidationDbContext> options) : DbContext(options)
    {
        public DbSet<Profile> Profiles => Set<Profile>();
    }

    [Fact]
    public void All_violations_are_reported_at_once()
    {
        using var database = new SqliteTestDatabase();
        using var ctx = new ValidationDbContext(database.BuildOptionsFor<ValidationDbContext>(
            o => o.UseEfInterceptors(s => s.WithValidation())).Options);
        ctx.Database.EnsureCreated();

        ctx.Profiles.Add(new Profile { Login = "", Age = 999 });

        var failure = Assert.Throws<EntityValidationException>(() => ctx.SaveChanges());
        var violations = failure.Failures.Single().Value;
        Assert.Equal(2, violations.Length);
        Assert.Contains(violations, m => m.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Valid_entities_save_normally()
    {
        using var database = new SqliteTestDatabase();
        using var ctx = new ValidationDbContext(database.BuildOptionsFor<ValidationDbContext>(
            o => o.UseEfInterceptors(s => s.WithValidation())).Options);
        ctx.Database.EnsureCreated();

        ctx.Profiles.Add(new Profile { Login = "ok", Age = 42 });
        ctx.SaveChanges();

        Assert.Single(ctx.Profiles);
    }
}

public class SlowSaveChangesDetectorTests
{
    [Fact]
    public void Warnings_appear_when_threshold_exceeded()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        using var db = new SqliteTestDatabase().CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.WithSlowSaves(TimeSpan.Zero, factory));
        });

        db.Database.EnsureCreated();
        db.TenantPets.Add(new TenantPet { Name = "Slow" });
        db.SaveChanges();

        Assert.Contains(provider.Records,
            r => r.Level == LogLevel.Warning && r.Message.Contains("Slow SaveChanges detected"));
    }
}

public class ModelBuilderFilterExtensionTests
{
    private class FilteredDbContext(
        DbContextOptions<FilteredDbContext> options,
        ITenantProvider tenants) : DbContext(options)
    {
        public DbSet<Cat> Cats => Set<Cat>();
        public DbSet<TenantPet> Pets => Set<TenantPet>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Match the seed context's table names.
            modelBuilder.Entity<TenantPet>().ToTable("TenantPets");

            modelBuilder.ApplySoftDeleteFilters();
            modelBuilder.ApplyTenantFilters(tenants);
        }
    }

    /// <summary>Exercises the AndAlso merge with a pre-existing anonymous filter.</summary>
    private class MergedDbContext(DbContextOptions<MergedDbContext> options) : DbContext(options)
    {
        public DbSet<Cat> Cats => Set<Cat>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Cat>().HasQueryFilter(c => c.Name != "Hidden");
            modelBuilder.ApplySoftDeleteFilters();   // must MERGE, not overwrite
        }
    }

    [Fact]
    public void Soft_delete_and_tenant_filters_compose()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "DeletedCat", IsDeleted = true });
            seed.TenantPets.Add(new TenantPet { Name = "ForeignPet", TenantId = "other" });
            seed.TenantPets.Add(new TenantPet { Name = "Mine", TenantId = "acme" });
            seed.SaveChanges();
        }

        using var filtered = new FilteredDbContext(
            database.BuildOptionsFor<FilteredDbContext>().Options,
            new StaticTenantProvider("acme"));

        // Soft-deleted cat is invisible, only current tenant's pet visible.
        Assert.Empty(filtered.Cats);
        Assert.Equal("Mine", filtered.Pets.Single().Name);
        Assert.Equal(2, filtered.Pets.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void New_filter_merges_with_existing_anonymous_filter()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "Hidden" });
            seed.Cats.Add(new Cat { Name = "DeletedOne", IsDeleted = true });
            seed.Cats.Add(new Cat { Name = "Visible" });
            seed.SaveChanges();
        }

        using var merged = new MergedDbContext(database.BuildOptionsFor<MergedDbContext>().Options);

        Assert.Equal(["Visible"], merged.Cats.Select(c => c.Name).ToList());
    }
}

public class ConstructorFactoryInstantiationBindingTests
{
    /// <summary>
    /// Dedicated context: an unbindable constructor breaks model building,
    /// so it must not live on the shared TestDbContext.
    /// </summary>
    private class LegacyDbContext(DbContextOptions<LegacyDbContext> options) : DbContext(options)
    {
        public DbSet<LegacyItem> Items => Set<LegacyItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<LegacyItem>().ToTable("LegacyItems");
    }

    [Fact]
    public void Factory_binding_replaces_constructor_and_binds_columns()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new LegacyDbContext(database.BuildOptionsFor<LegacyDbContext>().Options))
        {
            seed.Database.EnsureCreated();
            seed.Database.ExecuteSqlRaw("INSERT INTO LegacyItems (Id, Name) VALUES (1, 'FromDb')");
        }

        var factoryCalls = 0;

        using var ctx = new LegacyDbContext(database.BuildOptionsFor<LegacyDbContext>(
            o => o.UseEfInterceptors(s => s.WithConstructorFactories(
                new Dictionary<Type, Func<object>>
                {
                    [typeof(LegacyItem)] = () =>
                    {
                        Interlocked.Increment(ref factoryCalls);
                        return new LegacyItem("from-factory");
                    }
                }))).Options);

        var item = ctx.Items.AsNoTracking().Single();

        // The instance was created by the factory...
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
        // ...and column values were bound on top of it.
        Assert.Equal(1, item.Id);
        Assert.Equal("FromDb", item.Name);
    }
}
