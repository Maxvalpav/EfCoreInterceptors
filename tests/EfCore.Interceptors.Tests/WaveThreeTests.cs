using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Observability;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors.Tests;

public class DeleteGuardSaveChangesInterceptorTests
{
    [Fact]
    public void Protected_entities_cannot_be_deleted()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithDeleteGuard()));

        var entry = new LedgerEntry { Amount = 100m };
        db.LedgerEntries.Add(entry);
        db.SaveChanges();

        db.LedgerEntries.Remove(entry);
        Assert.Throws<ProtectedEntityException>(() => db.SaveChanges());
    }

    [Fact]
    public void Other_entity_types_delete_normally()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithDeleteGuard()));

        var pet = new TenantPet { Name = "Free" };
        db.TenantPets.Add(pet);
        db.SaveChanges();
        db.TenantPets.Remove(pet);
        db.SaveChanges();

        Assert.Equal(EntityState.Detached, db.Entry(pet).State);
    }
}

public class ImmutableEntityGuardSaveChangesInterceptorTests
{
    [Fact]
    public void Modifications_of_immutable_entities_are_rejected()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithImmutableGuard()));

        var record = new AuditRecord { Message = "posted" };
        db.AuditRecords.Add(record);
        db.SaveChanges();                       // insert is allowed

        record.Message = "rewritten";
        Assert.Throws<ImmutableEntityException>(() => db.SaveChanges());
    }
}

public class SaveChangesMetricsInterceptorTests
{
    [Fact]
    public void Save_metrics_are_published()
    {
        var executed = 0L;
        var entities = 0L;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name is "ef.save.executed" or "ef.save.entities")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
        {
            switch (inst.Name)
            {
                case "ef.save.executed": Interlocked.Add(ref executed, value); break;
                case "ef.save.entities": Interlocked.Add(ref entities, value); break;
            }
        });
        listener.Start();

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithSaveChangesMetrics()));

        db.Database.EnsureCreated();
        db.TenantPets.Add(new TenantPet { Name = "M1" });
        db.TenantPets.Add(new TenantPet { Name = "M2" });
        db.SaveChanges();

        Assert.True(Volatile.Read(ref executed) >= 1);
        Assert.True(Volatile.Read(ref entities) >= 2);
    }
}

public class TransactionMetricsAndWatchdogTests
{
    [Fact]
    public void Rollbacks_are_counted()
    {
        var rolledBack = 0L;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "ef.transaction.rolledback")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref rolledBack, value));
        listener.Start();

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithTransactionMetrics()));

        db.Database.EnsureCreated();
        using var tx = db.Database.BeginTransaction();
        tx.Rollback();

        Assert.True(Volatile.Read(ref rolledBack) >= 1);
    }

    [Fact]
    public void Long_transactions_produce_warnings()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithLongRunningTransactionDetection(TimeSpan.Zero, factory)));

        db.Database.EnsureCreated();
        using var tx = db.Database.BeginTransaction();
        tx.Commit();

        Assert.Contains(provider.Records,
            r => r.Level == LogLevel.Warning && r.Message.Contains("Long-running transaction"));
    }
}

public class CommandTimeoutCommandInterceptorTests
{
    private sealed class CapturingTimeoutInterceptor(Func<string, int?> selector)
        : CommandTimeoutCommandInterceptor(selector)
    {
        public int? LastApplied { get; private set; }

        public void Reset() => LastApplied = null;

        protected override void Apply(DbCommand command)
        {
            var before = command.CommandTimeout;
            base.Apply(command);
            LastApplied = command.CommandTimeout == before ? null : command.CommandTimeout;
        }
    }

    [Fact]
    public void Tagged_queries_get_configured_timeout()
    {
        var interceptor = new CapturingTimeoutInterceptor(
            CommandTimeoutCommandInterceptor.FromTags(new Dictionary<string, int> { ["report"] = 300 }));

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.Add(interceptor)));

        db.Database.EnsureCreated();

        db.Reports.TagWith("report").ToList();
        Assert.Equal(300, interceptor.LastApplied);

        interceptor.Reset();
        db.Reports.ToList();
        Assert.Null(interceptor.LastApplied);   // untagged query keeps context default
    }
}

public class InitializationMaterializationInterceptorTests
{
    [Fact]
    public void OnLoaded_hook_runs_on_materialization()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Reports.Add(new Report { Title = "R" });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithInitialization())).Options);

        var report = ctx.Reports.Single();
        Assert.True(report.LoadedHookRan);
    }
}

public class PropertyEncryptionInterceptorTests
{
    private static readonly string KeyBase64 = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Encrypted_properties_roundtrip_and_store_ciphertext()
    {
        using var database = new SqliteTestDatabase();
        var optionsAction = new Action<DbContextOptionsBuilder>(o =>
            o.UseEfInterceptors(s => s.WithPropertyEncryption(new AesGcmPropertyValueEncryptor(KeyBase64))));

        int itemId;
        using (var writeCtx = new TestDbContext(database.BuildOptions(optionsAction).Options))
        {
            writeCtx.Database.EnsureCreated();
            var item = new VaultItem { Name = "Card holder", CardNumber = "4111-1111-1111-1111" };
            writeCtx.VaultItems.Add(item);
            writeCtx.SaveChanges();
            itemId = item.Id;
        }

        // Raw row contains ciphertext, not the plaintext card number.
        using (var rawCtx = new TestDbContext(database.BuildOptions().Options))
        {
            var stored = rawCtx.VaultItems.AsNoTracking().Single(i => i.Id == itemId).CardNumber;
            Assert.NotNull(stored);
            Assert.NotEqual("4111-1111-1111-1111", stored);
            Assert.DoesNotContain("4111", stored);
        }

        // Fresh load decrypts transparently.
        using (var readCtx = new TestDbContext(database.BuildOptions(optionsAction).Options))
        {
            var loaded = readCtx.VaultItems.AsNoTracking().Single(i => i.Id == itemId);
            Assert.Equal("4111-1111-1111-1111", loaded.CardNumber);
        }
    }

    [Fact]
    public void Updates_reencrypt_modified_values()
    {
        using var database = new SqliteTestDatabase();
        var optionsAction = new Action<DbContextOptionsBuilder>(o =>
            o.UseEfInterceptors(s => s.WithPropertyEncryption(new AesGcmPropertyValueEncryptor(KeyBase64))));

        using (var writeCtx = new TestDbContext(database.BuildOptions(optionsAction).Options))
        {
            writeCtx.Database.EnsureCreated();
            writeCtx.VaultItems.Add(new VaultItem { Name = "N", CardNumber = "AAAA" });
            writeCtx.SaveChanges();

            var item = writeCtx.VaultItems.Single();
            item.CardNumber = "BBBB";
            writeCtx.SaveChanges();
        }

        using (var readCtx = new TestDbContext(database.BuildOptions(optionsAction).Options))
        {
            Assert.Equal("BBBB", readCtx.VaultItems.AsNoTracking().Single().CardNumber);
        }
    }
}
