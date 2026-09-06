using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Tests;

/// <summary>Audit wave 3 gaps: outbox keys/retention, blind index search, source-generated contracts.</summary>
public sealed class OutboxKeysTests
{
    private sealed record ShipmentCreated(string ShipmentId, long Sequence) : IDomainEvent, IHasEventIdentity
    {
        public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
        public string AggregateId => ShipmentId;
        public long Version => Sequence;
    }

    private sealed record Pinged : IDomainEvent
    {
        public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
    }

    [Fact]
    public void Identity_events_have_stable_keys()
    {
        var first = new ShipmentCreated("s-1", 7);
        var same = new ShipmentCreated("s-1", 7);
        var next = new ShipmentCreated("s-1", 8);

        Assert.Equal(OutboxEventKeys.IdempotencyKeyFor(first), OutboxEventKeys.IdempotencyKeyFor(same));
        Assert.NotEqual(OutboxEventKeys.IdempotencyKeyFor(first), OutboxEventKeys.IdempotencyKeyFor(next));
        Assert.Equal("s-1", OutboxEventKeys.PartitionKeyFor(first));
    }

    [Fact]
    public void Plain_events_have_unique_keys_and_type_partition()
    {
        Assert.NotEqual(OutboxEventKeys.IdempotencyKeyFor(new Pinged()), OutboxEventKeys.IdempotencyKeyFor(new Pinged()));
        Assert.Contains("Pinged", OutboxEventKeys.PartitionKeyFor(new Pinged()));
    }

    [Fact]
    public async Task Producer_stamps_keys_on_rows()
    {
        using var sqlite = new SqliteTestDatabase();
        var options = sqlite.BuildOptions(o => o.AddInterceptors(new OutboxSaveChangesInterceptor()));

        using (var db = new TestDbContext(options.Options))
        {
            db.Database.EnsureCreated();
            var kennel = new Kennel { Title = "k1" };
            kennel.AddDomainEvent(new ShipmentCreated("agg-9", 1));
            db.Kennels.Add(kennel);
            await db.SaveChangesAsync();
        }

        using (var db = new TestDbContext(options.Options))
        {
            var row = db.OutboxMessages.AsNoTracking().Single();
            Assert.Equal("agg-9", row.PartitionKey);
            Assert.Equal(OutboxEventKeys.IdempotencyKeyFor(new ShipmentCreated("agg-9", 1)), row.IdempotencyKey);
        }
    }

    [Fact]
    public async Task Retention_purges_only_expired_rows()
    {
        using var sqlite = new SqliteTestDatabase();
        var old = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        using (var db = sqlite.CreateContext())
        {
            db.OutboxMessages.AddRange(
                new OutboxMessage { Type = "old-delivered", PayloadJson = "{}", OccurredAtUtc = old, ProcessedAtUtc = old },
                new OutboxMessage { Type = "fresh-delivered", PayloadJson = "{}", OccurredAtUtc = now, ProcessedAtUtc = now },
                new OutboxMessage { Type = "pending", PayloadJson = "{}", OccurredAtUtc = now },
                new OutboxMessage { Type = "old-dead", PayloadJson = "{}", OccurredAtUtc = old, DeadLetteredAtUtc = old });
            await db.SaveChangesAsync();
        }

        using (var db = sqlite.CreateContext())
        {
            Assert.Equal(1, await OutboxMaintenance.DeleteDeliveredOlderThanAsync(db, now.AddDays(-30)));
            Assert.Equal(1, await OutboxMaintenance.DeleteDeadLetteredOlderThanAsync(db, now.AddDays(-30)));
            Assert.Equal(2, db.OutboxMessages.Count());
        }
    }

    [Fact]
    public void Index_configuration_registers_claim_indexes()
    {
        var options = new DbContextOptionsBuilder<IndexedOutboxDb>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var indexed = new IndexedOutboxDb(options);
        var indexedEntity = indexed.Model.FindEntityType(typeof(OutboxMessage));
        Assert.NotNull(indexedEntity);
        Assert.True(indexedEntity!.GetIndexes().Count() >= 4);
        Assert.Contains(indexedEntity.GetIndexes(), i => i.Properties.Any(p => p.Name == nameof(OutboxMessage.IdempotencyKey)));
    }

    private sealed class IndexedOutboxDb(DbContextOptions<IndexedOutboxDb> options) : DbContext(options)
    {
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new OutboxMessageTypeConfiguration());
    }
}

public sealed class BlindIndexTests
{
    private static readonly string IndexKey = Convert.ToBase64String(new byte[32]);
    private static readonly string EncKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());

    public sealed class Contact
    {
        public int Id { get; set; }

        [Encrypted]
        public string? Email { get; set; }

        [BlindIndexOf(nameof(Email))]
        public string? EmailHash { get; set; }
    }

    private sealed class ContactDb(DbContextOptions<ContactDb> options) : DbContext(options)
    {
        public DbSet<Contact> Contacts => Set<Contact>();
    }

    private static BlindIndexOptions Options() => new(IndexKey, "contacts");

    [Fact]
    public void Tokens_are_stable_distinct_and_null_safe()
    {
        Assert.Equal(HmacBlindIndex.For("a@x.io", IndexKey, "contacts"), HmacBlindIndex.For("a@x.io", IndexKey, "contacts"));
        Assert.NotEqual(HmacBlindIndex.For("a@x.io", IndexKey, "contacts"), HmacBlindIndex.For("b@x.io", IndexKey, "contacts"));
        Assert.NotEqual(HmacBlindIndex.For("a@x.io", IndexKey, "c1"), HmacBlindIndex.For("a@x.io", IndexKey, "c2"));
        Assert.Null(HmacBlindIndex.For(null, IndexKey, "contacts"));
    }

    [Fact]
    public async Task Encrypted_email_is_searchable_by_hash()
    {
        using var sqlite = new SqliteTestDatabase();
        using var encryptor = new AesGcmPropertyValueEncryptor(EncKey);
        var options = sqlite.BuildOptionsFor<ContactDb>(o => o.AddInterceptors(
            new PropertyEncryptionSaveChangesInterceptor(encryptor, Options())));

        using (var db = new ContactDb(options.Options))
        {
            db.Database.EnsureCreated();
            db.Contacts.Add(new Contact { Email = "alice@example.com" });
            await db.SaveChangesAsync();
        }

        var token = HmacBlindIndex.For("alice@example.com", IndexKey, "contacts");
        using (var db = new ContactDb(options.Options))
        {
            var found = await db.Contacts.SingleOrDefaultAsync(c => c.EmailHash == token);
            Assert.NotNull(found);
            // Column holds ciphertext, index holds the token.
            Assert.NotEqual("alice@example.com", found!.Email);
            Assert.Equal(token, found.EmailHash);
            Assert.Equal("alice@example.com", encryptor.Decrypt(found.Email));
        }

        // No match on a wrong token.
        using (var db = new ContactDb(options.Options))
        {
            Assert.Null(await db.Contacts.SingleOrDefaultAsync(
                c => c.EmailHash == HmacBlindIndex.For("bob@example.com", IndexKey, "contacts")));
        }
    }

    [Fact]
    public async Task Hash_updates_with_email()
    {
        using var sqlite = new SqliteTestDatabase();
        using var encryptor = new AesGcmPropertyValueEncryptor(EncKey);
        var options = sqlite.BuildOptionsFor<ContactDb>(o => o.AddInterceptors(
            new PropertyEncryptionSaveChangesInterceptor(encryptor, Options())));

        int id;
        using (var db = new ContactDb(options.Options))
        {
            db.Database.EnsureCreated();
            var contact = new Contact { Email = "a@x.io" };
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
            id = contact.Id;
        }

        using (var db = new ContactDb(options.Options))
        {
            var contact = await db.Contacts.SingleAsync(c => c.Id == id);
            contact.Email = "b@x.io";
            await db.SaveChangesAsync();
        }

        using (var db = new ContactDb(options.Options))
        {
            var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == id);
            Assert.Equal(HmacBlindIndex.For("b@x.io", IndexKey, "contacts"), contact.EmailHash);
        }
    }
}

[Auditable, SoftDeletable, HasDomainEvents]
public sealed partial class GeneratedOrder
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public sealed class GeneratedContractTests
{
    private sealed class GeneratedDb(DbContextOptions<GeneratedDb> options) : DbContext(options)
    {
        public DbSet<GeneratedOrder> Orders => Set<GeneratedOrder>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<GeneratedOrder>().HasQueryFilter(e => !e.IsDeleted);
    }

    [Fact]
    public async Task Generated_contracts_drive_interceptors()
    {
        Assert.True(typeof(IAuditableEntity).IsAssignableFrom(typeof(GeneratedOrder)));
        Assert.True(typeof(ISoftDeletableEntity).IsAssignableFrom(typeof(GeneratedOrder)));
        Assert.True(typeof(IHasDomainEvents).IsAssignableFrom(typeof(GeneratedOrder)));

        using var sqlite = new SqliteTestDatabase();
        var dispatcher = new RecordingDispatcher();
        var options = sqlite.BuildOptionsFor<GeneratedDb>(o => o.AddInterceptors(
            new AuditSaveChangesInterceptor(),
            new SoftDeleteSaveChangesInterceptor(),
            new DomainEventsSaveChangesInterceptor(dispatcher)));

        int id;
        using (var db = new GeneratedDb(options.Options))
        {
            db.Database.EnsureCreated();
            var order = new GeneratedOrder { Title = "g1" };
            order.AddDomainEvent(new KennelBarked("woof"));
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            id = order.Id;
            Assert.NotEqual(default, order.CreatedAtUtc);
            Assert.Empty(order.DomainEvents);
        }
        Assert.Single(dispatcher.Dispatched);

        using (var db = new GeneratedDb(options.Options))
        {
            db.Orders.Remove(await db.Orders.SingleAsync(o => o.Id == id));
            await db.SaveChangesAsync();
        }
        using (var db = new GeneratedDb(options.Options))
        {
            Assert.Empty(await db.Orders.ToListAsync());
            Assert.True((await db.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == id)).IsDeleted);
        }
    }

    private sealed record KennelBarked(string Sound) : IDomainEvent
    {
        public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
    }
}
