using System.Data;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Observability;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.Tests;

/// <summary>
/// Regression tests for deep-audit P0 fixes (docs 05–07, doc 10.3):
/// outbox claim-token + dead-letter, cache size limits, bulk encrypted update,
/// AAD fail-closed defaults.
/// </summary>
public class AuditP0OutboxTests
{
    private sealed class AlwaysFailHandler : IOutboxMessageHandler
    {
        public ValueTask HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
            => throw new InvalidOperationException("poison");
    }

    private sealed class OkHandler : IOutboxMessageHandler
    {
        public List<long> Delivered { get; } = [];
        public ValueTask HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Delivered.Add(message.Id);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Poison_message_is_dead_lettered_after_max_attempts()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.OutboxMessages.Add(new OutboxMessage
            {
                Type = "poison",
                PayloadJson = "{}",
                OccurredAtUtc = DateTimeOffset.UtcNow,
            });
            seed.SaveChanges();
        }

        var handler = new AlwaysFailHandler();
        var sp = new ServiceCollection();
        sp.AddScoped(_ => sqlite.CreateContext());
        // Rebind TestDbContext resolution to the shared-connection context:
        sp.AddScoped<IOutboxMessageHandler>(_ => handler);
        // OutboxProcessor<TContext> needs TContext resolvable — register factory-backed instance.
        // Use a scope factory shim:
        var provider = sp.BuildServiceProvider();
        var processor = new TestableOutboxProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(), maxAttempts: 2);

        // Two failing cycles: attempt 0->1 (retry), 1->2 (dead-letter).
        await processor.RunBatchAsync();
        using (var check = sqlite.CreateContext())
        {
            var msg = check.OutboxMessages.Single();
            Assert.Null(msg.DeadLetteredAtUtc);
            Assert.Equal(1, msg.AttemptCount);
        }

        await processor.RunBatchAsync();
        using (var check = sqlite.CreateContext())
        {
            var msg = check.OutboxMessages.Single();
            Assert.NotNull(msg.DeadLetteredAtUtc);
            Assert.Equal(2, msg.AttemptCount);
            Assert.NotNull(msg.Error);
        }

        // Third cycle must NOT pick the dead-lettered row (no throw, no attempt bump).
        await processor.RunBatchAsync();
        using (var check = sqlite.CreateContext())
        {
            Assert.Equal(2, check.OutboxMessages.Single().AttemptCount);
        }
    }

    [Fact]
    public async Task Delivered_message_clears_claim_token()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.OutboxMessages.Add(new OutboxMessage
            {
                Type = "ok", PayloadJson = "{}", OccurredAtUtc = DateTimeOffset.UtcNow,
            });
            seed.SaveChanges();
        }

        var handler = new OkHandler();
        var sp = new ServiceCollection();
        sp.AddScoped(_ => sqlite.CreateContext());
        sp.AddScoped<IOutboxMessageHandler>(_ => handler);
        var processor = new TestableOutboxProcessor(
            sp.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), maxAttempts: 10);

        await processor.RunBatchAsync();

        using var check = sqlite.CreateContext();
        var msg = check.OutboxMessages.Single();
        Assert.NotNull(msg.ProcessedAtUtc);
        Assert.Null(msg.ClaimToken);
        Assert.Null(msg.LockedUntilUtc);
        Assert.Single(handler.Delivered);
    }

    /// <summary>Exposes the private batch pump without running the infinite host loop.</summary>
    private sealed class TestableOutboxProcessor(IServiceScopeFactory scopeFactory, int maxAttempts)
        : OutboxProcessor<TestDbContext>(scopeFactory, TimeSpan.FromMilliseconds(10), 20, null, TimeProvider.System, maxAttempts)
    {
        public Task RunBatchAsync()
        {
            var method = typeof(OutboxProcessor<TestDbContext>).GetMethod(
                "ProcessPendingBatchAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            return (Task)method.Invoke(this, [CancellationToken.None])!;
        }
    }
}

public class AuditP0BulkEncryptionTests
{
    private static string Key() => Convert.ToBase64String(new byte[32]);

    [Fact]
    public async Task Encrypted_bulk_update_roundtrips_through_decryptor()
    {
        using var sqlite = new SqliteTestDatabase();
        var encryptor = new AesGcmPropertyValueEncryptor(Key());
        using (var seed = sqlite.CreateContext())
        {
            seed.VaultItems.Add(new VaultItem { Name = "a", CardNumber = "plain" });
            seed.SaveChanges();
        }

        using (var db = sqlite.CreateContext())
        {
            var n = await db.VaultItems.ExecuteEncryptedUpdateAsync(
                v => v.CardNumber, "4111111111111111", encryptor);
            Assert.Equal(1, n);
        }

        using (var db = sqlite.CreateContext())
        {
            var raw = db.VaultItems.AsNoTracking().Single();
            Assert.NotEqual("4111111111111111", raw.CardNumber); // ciphertext at rest
            Assert.Equal("4111111111111111", encryptor.Decrypt(raw.CardNumber));
        }
    }

    [Fact]
    public async Task Encrypted_bulk_update_refuses_plain_property()
    {
        using var sqlite = new SqliteTestDatabase();
        var encryptor = new AesGcmPropertyValueEncryptor(Key());
        using var db = sqlite.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.VaultItems.ExecuteEncryptedUpdateAsync(v => v.Name, "x", encryptor));
    }

    [Fact]
    public async Task Encrypted_bulk_update_refuses_double_encryption()
    {
        using var sqlite = new SqliteTestDatabase();
        var encryptor = new AesGcmPropertyValueEncryptor(Key());
        var cipher = encryptor.Encrypt("already");
        using var db = sqlite.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.VaultItems.ExecuteEncryptedUpdateAsync(v => v.CardNumber, cipher, encryptor));
    }
}

public class AuditP0CacheTests
{
    [Fact]
    public void Oversize_result_is_served_but_not_stored()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            for (var i = 0; i < 20; i++) seed.Cats.Add(new Cat { Name = "c" + i });
            seed.SaveChanges();
        }

        var cache = new CachingCommandInterceptor(
            TimeSpan.FromSeconds(30), true, false, 1000, null,
            maxRowsPerEntry: 5, maxBytesPerEntry: 1024 * 1024);
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            var rows = db.Cats.AsNoTracking().ToList();
            Assert.Equal(20, rows.Count); // still served
        }
        Assert.Equal(0, cache.Count); // but bypassed the store (06.2)
    }

    [Fact]
    public void Small_result_is_cached()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.Cats.Add(new Cat { Name = "tiny" });
            seed.SaveChanges();
        }

        var cache = new CachingCommandInterceptor(TimeSpan.FromSeconds(30));
        using (var db = sqlite.CreateContext(o => o.AddInterceptors(cache)))
        {
            Assert.Single(db.Cats.AsNoTracking().ToList());
        }
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Cached_byte_array_is_defensively_copied()
    {
        var result = new CachedQueryResult(["bin"], [typeof(byte[])],
            [new object[] { new byte[] { 1, 2, 3 } }]);
        using var reader = new CachedDataReader(result);
        Assert.True(reader.Read());
        var first = reader.GetFieldValue<byte[]>(0);
        first[0] = 99;
        Assert.Equal(1, ((byte[])result.Rows[0][0])[0]); // cache entry untouched (06.6)
    }
}

public class AuditP0EncryptionContractTests
{
    private sealed class LegacyCustomEncryptor : IPropertyValueEncryptor
    {
        public string? Encrypt(string? plaintext) => "x" + plaintext;
        public string? Decrypt(string? ciphertext) => ciphertext?[1..];
    }

    [Fact]
    public void Aad_overloads_fail_closed_by_default()
    {
        IPropertyValueEncryptor enc = new LegacyCustomEncryptor();
        Assert.Throws<NotSupportedException>(() => enc.Encrypt("p", [1, 2, 3]));
        Assert.Throws<NotSupportedException>(() => enc.Decrypt("c", [1, 2, 3]));
    }

    [Fact]
    public void Span_key_ctor_roundtrips()
    {
        byte[] key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        using var enc = new AesGcmPropertyValueEncryptor((ReadOnlySpan<byte>)key);
        var cipher = enc.Encrypt("secret");
        Assert.Equal("secret", enc.Decrypt(cipher));
    }

    [Fact]
    public void Entity_carries_claim_and_dlq_columns()
    {
        var msg = new OutboxMessage();
        Assert.Null(msg.ClaimToken);
        Assert.Null(msg.DeadLetteredAtUtc);
        msg.ClaimToken = Guid.NewGuid();
        Assert.NotNull(msg.ClaimToken);
    }
}
