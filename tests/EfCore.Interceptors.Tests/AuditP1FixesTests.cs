using System.Text.Json;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Model;
using EfCore.Interceptors.Observability;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Testing;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.Tests;

/// <summary>Second audit wave (docs d/10.3 P1): dispatching, table invalidation, key rotation.</summary>
public class AuditP1DispatchTests
{
    public sealed record OrderPaid(string OrderId) : IDomainEvent
    {
        public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class PaidHandler : IOutboxEventHandler<OrderPaid>
    {
        public List<string> Seen { get; } = [];
        public ValueTask HandleAsync(OrderPaid evt, OutboxMessage message, CancellationToken ct)
        {
            Seen.Add(evt.OrderId);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Dispatches_to_typed_handler()
    {
        var services = new ServiceCollection();
        var inner = new PaidHandler();
        services.AddSingleton<IOutboxEventHandler<OrderPaid>>(inner);
        var provider = services.BuildServiceProvider();
        var dispatcher = new DispatchingOutboxMessageHandler(provider);

        var evt = new OrderPaid("ord-1");
        await dispatcher.HandleAsync(new OutboxMessage
        {
            Id = 1,
            Type = typeof(OrderPaid).FullName!,
            PayloadJson = JsonSerializer.Serialize(evt),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Single(inner.Seen);
        Assert.Equal("ord-1", inner.Seen[0]);
    }

    [Fact]
    public async Task Missing_handler_throws_for_dlq_path()
    {
        var dispatcher = new DispatchingOutboxMessageHandler(
            new ServiceCollection().BuildServiceProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.HandleAsync(new OutboxMessage
        {
            Id = 2,
            Type = typeof(OrderPaid).FullName!,
            PayloadJson = JsonSerializer.Serialize(new OrderPaid("x")),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None).AsTask());
    }
}

public class AuditP1CacheInvalidationTests
{
    [Fact]
    public void Write_evicts_only_affected_table()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.Cats.Add(new Cat { Name = "c1" });
            seed.Kennels.Add(new Kennel { Title = "k1" });
            seed.SaveChanges();
        }

        var cache = new CachingCommandInterceptor(TimeSpan.FromMinutes(5), true, invalidateOnWrites: true);
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            Assert.Single(db.Cats.AsNoTracking().ToList());
            Assert.Single(db.Kennels.AsNoTracking().ToList());
        }
        Assert.Equal(2, cache.Count);

        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            db.Cats.Add(new Cat { Name = "c2" });
            db.SaveChanges(); // write to Cats only
        }

        // Cats entry evicted, Kennels entry intact (06.4).
        Assert.Equal(1, cache.Count);
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            Assert.Equal(2, db.Cats.AsNoTracking().ToList().Count); // fresh read
        }
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void InvalidateTable_drops_only_matching_entries()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.Cats.Add(new Cat { Name = "c1" });
            seed.Kennels.Add(new Kennel { Title = "k1" });
            seed.SaveChanges();
        }

        var cache = new CachingCommandInterceptor(TimeSpan.FromMinutes(5));
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            _ = db.Cats.AsNoTracking().ToList();
            _ = db.Kennels.AsNoTracking().ToList();
        }
        Assert.Equal(2, cache.Count);
        cache.InvalidateTable("Cats");
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TagWith_dep_contract_drives_eviction()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.Cats.Add(new Cat { Name = "c1" });
            seed.Kennels.Add(new Kennel { Title = "k1" });
            seed.SaveChanges();
        }

        var cache = new CachingCommandInterceptor(TimeSpan.FromMinutes(5), true, invalidateOnWrites: true);
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            // Cats query declares a dependency on Kennels (view/CTE contract).
            _ = db.Cats.TagWith("dep:Kennels").AsNoTracking().ToList();
        }
        Assert.Equal(1, cache.Count);

        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            db.Kennels.Add(new Kennel { Title = "k2" });
            db.SaveChanges(); // write to Kennels evicts the dep-tagged Cats entry
        }
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Table_parser_extracts_from_join_and_write_targets()
    {
        Assert.Contains("Cats", CachingCommandInterceptor.ParseReadTables(
            "SELECT c.Name, k.Title FROM Cats AS c INNER JOIN Kennels AS k ON k.Id = c.Id"));
        Assert.Contains("Orders", CachingCommandInterceptor.ParseWriteTables(
            "UPDATE Orders SET Total = 1 WHERE Id = 2"));
        Assert.Contains("Events", CachingCommandInterceptor.ParseWriteTables(
            "INSERT INTO Events (Id) VALUES (1)"));
        Assert.Contains("Logs", CachingCommandInterceptor.ParseReadTables(
            "-- dep:Logs\nSELECT 1"));
    }
}

public class AuditP1EncryptionRotationTests
{
    private static string Key(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    [Fact]
    public void Ring_reads_v1_and_writes_v2_with_kid()
    {
        var v1key = Key(1);
        using var v1 = new AesGcmPropertyValueEncryptor(v1key);
        var oldCipher = v1.Encrypt("secret")!;

        using var ring = new StaticKeyRing([v1key, Key(2)], currentKid: 1);
        using var enc = new KeyRingPropertyValueEncryptor(ring);
        Assert.Equal("secret", enc.Decrypt(oldCipher)); // legacy kid 0

        var fresh = enc.Encrypt("secret")!;
        var payload = Convert.FromBase64String(fresh);
        Assert.Equal(0x02, payload[0]); // v2 marker
        Assert.Equal(1, payload[1]);    // current kid
        Assert.Equal("secret", enc.Decrypt(fresh));
        Assert.True(enc.IsEncrypted(fresh));
    }

    [Fact]
    public void Unknown_kid_throws()
    {
        using var ring = new StaticKeyRing([Key(1)]);
        using var enc = new KeyRingPropertyValueEncryptor(ring);
        var payload = Convert.FromBase64String(enc.Encrypt("x")!);
        payload[1] = 7; // corrupt kid
        Assert.Throws<ArgumentOutOfRangeException>(
            () => enc.Decrypt(Convert.ToBase64String(payload)));
    }

    [Fact]
    public void Strict_rejects_legacy_and_callback_recovers_in_lenient()
    {
        using var enc = new AesGcmPropertyValueEncryptor(Key(3));
        var garbage = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9 }); // unversioned junk
        enc.MigrationMode = EncryptionMigrationMode.Strict;
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => enc.Decrypt(garbage));

        enc.MigrationMode = EncryptionMigrationMode.Lenient;
        enc.DecryptionFailed = (_, _) => "fallback";
        Assert.Equal("fallback", enc.Decrypt(garbage));
    }

    [Fact]
    public async Task ReEncrypt_migrates_column_to_new_key()
    {
        using var sqlite = new SqliteTestDatabase();
        var oldKey = Key(4);
        using var oldEnc = new AesGcmPropertyValueEncryptor(oldKey);
        using (var seed = sqlite.CreateContext())
        {
            seed.VaultItems.Add(new VaultItem { Name = "v", CardNumber = "4111111111111111" });
            seed.SaveChanges();
            // Simulate pre-existing v1-encrypted row (as PropertyEncryption interceptor would write).
            var row = seed.VaultItems.Single();
            row.CardNumber = oldEnc.Encrypt("4111111111111111");
            seed.SaveChanges();
        }

        using var ring = new StaticKeyRing([oldKey, Key(5)], currentKid: 1);
        using var newEnc = new KeyRingPropertyValueEncryptor(ring);
        using (var db = sqlite.CreateContext())
        {
            var (migrated, skipped) = await db.ReEncryptAsync(
                db.VaultItems.OrderBy(v => v.Id), v => v.CardNumber, oldEnc, newEnc);
            Assert.Equal(1, migrated);
            Assert.Equal(0, skipped);
        }

        using (var db = sqlite.CreateContext())
        {
            var raw = db.VaultItems.AsNoTracking().Single().CardNumber!;
            Assert.Equal(0x02, Convert.FromBase64String(raw)[0]);
            Assert.Equal("4111111111111111", newEnc.Decrypt(raw));
        }
    }
}

public class AuditP1MaintenanceTests
{
    [Fact]
    public async Task Retention_deletes_only_expired_rows()
    {
        using var sqlite = new SqliteTestDatabase();
        var now = DateTimeOffset.UtcNow;
        using (var seed = sqlite.CreateContext())
        {
            // Bypass interceptors: insert ChangeLogEntry rows directly.
            seed.ChangeLogEntries.AddRange(
                new ChangeLogEntry { EntityName = "A", EntityKey = "1", Action = "Added", ChangesJson = "[]", TimestampUtc = now.AddDays(-400) },
                new ChangeLogEntry { EntityName = "A", EntityKey = "2", Action = "Added", ChangesJson = "[]", TimestampUtc = now.AddDays(-10) });
            seed.SaveChanges();
        }

        using (var db = sqlite.CreateContext())
        {
            var deleted = await ChangeLogMaintenance.DeleteOlderThanAsync(db, now.AddDays(-365), batchSize: 10);
            Assert.Equal(1, deleted);
            Assert.Single(db.ChangeLogEntries.AsNoTracking().ToList());
        }
    }

    [Fact]
    public void TypeConfiguration_registers_indexes()
    {
        var builder = new ModelBuilder();
        builder.ApplyConfiguration(new ChangeLogEntryTypeConfiguration());
        var entity = builder.Model.FindEntityType(typeof(ChangeLogEntry))!;
        Assert.Contains(entity.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(["EntityName", "EntityKey"]));
        Assert.Contains(entity.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(["TimestampUtc"]));
    }
}

public class AuditP1PiiTests
{
    [Fact]
    public void Masks_email_phone_and_luhn_card()
    {
        var sql = "INSERT INTO Users (Email, Phone, Card) VALUES ('ivan.petrov@example.com', '+7 701 123-45-67', '4111111111111111')";
        var redacted = PiiRedactor.Default(sql);
        Assert.DoesNotContain("ivan.petrov@example.com", redacted);
        Assert.DoesNotContain("+7 701 123-45-67", redacted);
        Assert.DoesNotContain("4111111111111111", redacted);
        Assert.Contains("****-****-****-1111", redacted);
    }

    [Fact]
    public void Leaves_non_card_digit_runs_alone()
    {
        Assert.True(PiiRedactor.IsLuhnValid("4111111111111111"));
        Assert.False(PiiRedactor.IsLuhnValid("1234567890123456"));
        var sql = "SELECT * FROM Orders WHERE Id = 1234567890123456";
        Assert.Contains("1234567890123456", PiiRedactor.Default(sql));
    }
}

public class AuditP1TenancyTests
{
    [Fact]
    public void WithMultiTenancy_auto_registers_cache_key_factory()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithMultiTenancy(new StaticTenantProvider("t1"))));
        Assert.IsType<TenantModelCacheKeyFactory>(
            db.GetService<IModelCacheKeyFactory>());
    }
}

public class AuditP1TestingPackageTests
{
    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Fakes_cover_audit_identity()
    {
        var users = new FakeCurrentUserProvider("alice");
        Assert.Equal("alice", users.UserName);
        users.UserName = null;
        Assert.Null(users.UserName);
        Assert.Equal("tenant-1", new FakeTenantProvider().CurrentTenantId);
        var dispatcher = new RecordingDomainEventDispatcher();
        Assert.Empty(dispatcher.Dispatched);
    }

    [Fact]
    public void Encryptor_contracts_hold_for_both_encryptors()
    {
        var key = Convert.ToBase64String(new byte[32]);
        using var v1 = new AesGcmPropertyValueEncryptor(key);
        EncryptorContract.CheckRoundtrip(v1);
        EncryptorContract.CheckTamperDetected(v1);

        using var ring = new StaticKeyRing([key, Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray())], currentKid: 1);
        using var v2 = new KeyRingPropertyValueEncryptor(ring);
        EncryptorContract.CheckRoundtrip(v2);
        EncryptorContract.CheckTamperDetected(v2);
    }

    [Fact]
    public void Memory_store_honors_injected_clock()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StubClock(start);
        var store = new MemoryQueryCacheStore(TimeSpan.FromMinutes(5), 100, clock);
        var result = new CachedQueryResult(["a"], [typeof(int)], [new object[] { 1 }]);
        store.Set("k", result, TimeSpan.FromMinutes(5));
        Assert.True(store.TryGet("k", out _));
        clock.Now = start.AddHours(1);
        Assert.False(store.TryGet("k", out _));
    }
}
