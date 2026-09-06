using System.Data;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Tests;

/// <summary>Third audit wave (d/02 gaps): key contributors, outbox/domain-events combo, orphan policy, owned audit.</summary>
public sealed class CacheKeyContributorTests
{
    private static SqliteCommand TextCommand(string sql, string connectionString = "DataSource=:memory:")
    {
        var connection = new SqliteConnection(connectionString);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    [Fact]
    public void Tenant_contributors_isolate_keys()
    {
        using var command = TextCommand("SELECT * FROM Cats");
        var tenantA = new CachingCommandInterceptor();
        tenantA.AddContributor(new TenantKeyContributor(new StaticTenantProvider("a")));
        var tenantB = new CachingCommandInterceptor();
        tenantB.AddContributor(new TenantKeyContributor(new StaticTenantProvider("b")));

        var keyA = tenantA.BuildKey(command, null);
        var keyB = tenantB.BuildKey(command, null);
        Assert.NotEqual(keyA, keyB);
        Assert.Contains("|k:a", keyA);
        Assert.Contains("|k:b", keyB);

        // Same tenant -> same key; no contributors -> base key.
        var tenantA2 = new CachingCommandInterceptor();
        tenantA2.AddContributor(new TenantKeyContributor(new StaticTenantProvider("a")));
        Assert.Equal(keyA, tenantA2.BuildKey(command, null));
        Assert.Equal(CachingCommandInterceptor.BuildKey(command), new CachingCommandInterceptor().BuildKey(command, null));
    }

    [Fact]
    public void StripSecrets_redacts_passwords_but_keeps_identity()
    {
        var redacted = CachingCommandInterceptor.StripSecrets("Data Source=db;Password=secret123");
        Assert.DoesNotContain("secret123", redacted);
        Assert.Contains("***", redacted);

        using var commandOne = TextCommand("SELECT 1", "DataSource=:memory:;Password=one");
        using var commandTwo = TextCommand("SELECT 1", "DataSource=:memory:;Password=two");
        Assert.Equal(CachingCommandInterceptor.BuildKey(commandOne), CachingCommandInterceptor.BuildKey(commandTwo));
    }

    [Fact]
    public void ParseReadTables_ignores_comment_markers_inside_literals()
    {
        Assert.Equal(["Cats"], CachingCommandInterceptor.ParseReadTables("SELECT * FROM Cats WHERE Name = '-- JOIN Hacks'"));
        Assert.Equal(["Cats"], CachingCommandInterceptor.ParseReadTables("SELECT * FROM Cats WHERE Name = '/*' "));
    }

    [Fact]
    public void CachedDataReader_enumerates_data_records()
    {
        var reader = new CachedDataReader(new CachedQueryResult(["Id"], [typeof(int)], [new object[] { 1 }]));
        var count = 0;
        foreach (IDataRecord record in reader)
        {
            Assert.Equal(1, record.GetInt32(0));
            count++;
        }
        Assert.Equal(1, count);
    }

    [Fact]
    public void Invalidate_fragment_does_not_match_parameter_values()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.Cats.Add(new Cat { Name = "c1" });
            seed.SaveChanges();
        }

        var cache = new CachingCommandInterceptor(TimeSpan.FromMinutes(5));
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            // Closure variable -> DbParameter: the value 'Kennel' lands in the cache key
            // but is neither a read table nor SQL text (literals written inline WOULD be SQL text).
            var excluded = "Kennel";
            Assert.Single(db.Cats.Where(c => c.Name != excluded).AsNoTracking().ToList());
        }
        Assert.Equal(1, cache.Count);

        cache.Invalidate("Kennel");
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Read_your_writes_by_default_via_options()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.Cats.Add(new Cat { Name = "c1" });
            seed.SaveChanges();
        }

        var cache = new CachingCommandInterceptor(new CachingOptions { TimeToLive = TimeSpan.FromMinutes(5) });
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            Assert.Single(db.Cats.AsNoTracking().ToList());
        }
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            db.Cats.Add(new Cat { Name = "c2" });
            db.SaveChanges();
        }
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            Assert.Equal(2, db.Cats.AsNoTracking().ToList().Count);
        }
    }

    [Fact]
    public void Setup_options_overload_and_tenant_autowire_build()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext(o => o.UseEfInterceptors(s => s
            .WithMultiTenancy(new StaticTenantProvider("t1"), strictQueryPolicy: false)
            .WithSecondLevelCache(options => options.TimeToLive = TimeSpan.FromMinutes(1))));
        db.Database.EnsureCreated();
        Assert.Empty(db.Cats.AsNoTracking().ToList());
    }
}

public sealed class SoftDeletePolicyTests
{
    [Fact]
    public void Validation_runs_before_mutation()
    {
        using var sqlite = new SqliteTestDatabase();
        int catId;
        using (var seed = sqlite.CreateContext())
        {
            var cat = new Cat { Name = "c1" };
            seed.Cats.Add(cat);
            seed.LedgerEntries.Add(new LedgerEntry { Amount = 1 });
            seed.SaveChanges();
            catId = cat.Id;
        }

        using var db = sqlite.CreateContext(o => o.AddInterceptors(new SoftDeleteSaveChangesInterceptor()));
        var victim = db.Cats.Single(c => c.Id == catId);
        db.Cats.Remove(victim);
        db.LedgerEntries.Remove(db.LedgerEntries.Single()); // unrelated hard delete in the same unit of work
        var ex = Assert.Throws<SoftDeleteCascadeException>(() => db.SaveChanges());
        Assert.Contains("LedgerEntry", ex.Message);

        // Tracker left unmutated: soft-delete flags untouched, delete still pending.
        Assert.False(victim.IsDeleted);
        Assert.Equal(EntityState.Deleted, db.Entry(victim).State);
    }

    [Fact]
    public void AllowHardDelete_permits_mixed_units_of_work()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.Cats.Add(new Cat { Name = "c1" });
            seed.LedgerEntries.Add(new LedgerEntry { Amount = 5 });
            seed.SaveChanges();
        }

        using (var db = sqlite.CreateContext(o => o.AddInterceptors(
                   new SoftDeleteSaveChangesInterceptor(orphanPolicy: SoftDeleteOrphanPolicy.AllowHardDelete))))
        {
            db.Cats.Remove(db.Cats.Single());
            db.LedgerEntries.Remove(db.LedgerEntries.Single());
            db.SaveChanges();
        }

        using (var db = sqlite.CreateContext())
        {
            Assert.Empty(db.Cats.ToList());
            Assert.Empty(db.LedgerEntries.ToList());
            Assert.Single(db.Cats.IgnoreQueryFilters().ToList());
        }
    }

    [Fact]
    public void Repeat_remove_preserves_other_edits()
    {
        using var sqlite = new SqliteTestDatabase();
        int catId;
        using (var seed = sqlite.CreateContext())
        {
            var cat = new Cat { Name = "c1" };
            seed.Cats.Add(cat);
            seed.SaveChanges();
            catId = cat.Id;
        }

        using (var db = sqlite.CreateContext(o => o.AddInterceptors(new SoftDeleteSaveChangesInterceptor())))
        {
            db.Cats.Remove(db.Cats.Single(c => c.Id == catId));
            db.SaveChanges();
        }
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(new SoftDeleteSaveChangesInterceptor())))
        {
            var deleted = db.Cats.IgnoreQueryFilters().Single(c => c.Id == catId);
            deleted.Name = "renamed";
            db.Cats.Remove(deleted);
            db.SaveChanges();
        }

        using (var db = sqlite.CreateContext())
        {
            var row = db.Cats.IgnoreQueryFilters().Single(c => c.Id == catId);
            Assert.Equal("renamed", row.Name);
            Assert.True(row.IsDeleted);
        }
    }
}

public sealed class OutboxDomainEventsComboTests
{
    [Fact]
    public async Task Outbox_and_domain_events_fire_together()
    {
        using var sqlite = new SqliteTestDatabase();
        var dispatcher = new RecordingDispatcher();
        var options = sqlite.BuildOptions(o => o.AddInterceptors(
            new OutboxSaveChangesInterceptor(),
            new DomainEventsSaveChangesInterceptor(dispatcher)));

        using (var db = new TestDbContext(options.Options))
        {
            db.Database.EnsureCreated();
            var kennel = new Kennel { Title = "k1" };
            kennel.AddDomainEvent(new Barked("woof"));
            db.Kennels.Add(kennel);
            await db.SaveChangesAsync();
        }

        Assert.Single(dispatcher.Dispatched);
        using (var db = new TestDbContext(options.Options))
        {
            Assert.Single(db.OutboxMessages.ToList());
        }

        // No duplicates on a quiet save.
        using (var db = new TestDbContext(options.Options))
        {
            await db.SaveChangesAsync();
            Assert.Single(db.OutboxMessages.ToList());
        }
        Assert.Single(dispatcher.Dispatched);
    }

    [Fact]
    public async Task Two_saves_in_one_transaction_accumulate()
    {
        using var sqlite = new SqliteTestDatabase();
        var dispatcher = new RecordingDispatcher();
        var options = sqlite.BuildOptions(o => o.AddInterceptors(
            new DomainEventsSaveChangesInterceptor(dispatcher)));

        using (var db = new TestDbContext(options.Options))
        {
            db.Database.EnsureCreated();
            using var tx = await db.Database.BeginTransactionAsync();
            var first = new Kennel { Title = "k1" };
            first.AddDomainEvent(new Barked("one"));
            db.Kennels.Add(first);
            await db.SaveChangesAsync();
            var second = new Kennel { Title = "k2" };
            second.AddDomainEvent(new Barked("two"));
            db.Kennels.Add(second);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        Assert.Equal(2, dispatcher.Dispatched.Count);
    }

    [Fact]
    public async Task Rollback_restores_events()
    {
        using var sqlite = new SqliteTestDatabase();
        var dispatcher = new RecordingDispatcher();
        var options = sqlite.BuildOptions(o => o.AddInterceptors(
            new DomainEventsSaveChangesInterceptor(dispatcher)));

        Kennel kennel;
        using (var db = new TestDbContext(options.Options))
        {
            db.Database.EnsureCreated();
            using var tx = await db.Database.BeginTransactionAsync();
            kennel = new Kennel { Title = "k1" };
            kennel.AddDomainEvent(new Barked("woof"));
            db.Kennels.Add(kennel);
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        Assert.Empty(dispatcher.Dispatched);
        Assert.Single(kennel.DomainEvents);
    }
}

public sealed class OwnedAuditTests
{
    public sealed class OwnedAddress
    {
        public string City { get; set; } = string.Empty;
    }

    public sealed class OwnedOwner : IAuditableEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public OwnedAddress Address { get; set; } = new();
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
    }

    private sealed class OwnedDb(DbContextOptions<OwnedDb> options) : DbContext(options)
    {
        public DbSet<OwnedOwner> Owners => Set<OwnedOwner>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<OwnedOwner>().OwnsOne(o => o.Address);
    }

    [Fact]
    public void Owned_only_change_refreshes_owner_stamp()
    {
        using var sqlite = new SqliteTestDatabase();
        var audit = new AuditSaveChangesInterceptor();
        var options = sqlite.BuildOptionsFor<OwnedDb>(o => o.AddInterceptors(audit));

        var old = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var db = new OwnedDb(options.Options))
        {
            db.Database.EnsureCreated();
            db.Owners.Add(new OwnedOwner { Name = "n", Address = new OwnedAddress { City = "A" }, UpdatedAtUtc = old });
            db.SaveChanges();
        }

        using (var db = new OwnedDb(options.Options))
        {
            var owner = db.Owners.Single();
            Assert.Equal(old, owner.UpdatedAtUtc);
            owner.Address.City = "B";
            db.SaveChanges();
            Assert.NotNull(owner.UpdatedAtUtc);
            Assert.NotEqual(old, owner.UpdatedAtUtc);
        }
    }
}
