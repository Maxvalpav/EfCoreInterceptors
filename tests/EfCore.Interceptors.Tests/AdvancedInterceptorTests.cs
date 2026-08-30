using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Queries;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using EfCore.Interceptors.Tracking;
using EfCore.Interceptors.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCore.Interceptors.Tests;

public class ChangeLogSaveChangesInterceptorTests
{
    [Fact]
    public void Insert_and_update_are_recorded_with_property_diffs()
    {
        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s =>
            s.WithChangeLog(new StaticCurrentUserProvider("auditor"))));

        var cat = new Cat { Name = "Felix" };
        db.Cats.Add(cat);
        db.SaveChanges();

        cat.Name = "Garfield";
        db.SaveChanges();

        var entries = db.ChangeLogEntries.OrderBy(e => e.Id).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal("Added", entries[0].Action);
        Assert.Equal("Cat", entries[0].EntityName);
        Assert.Equal("auditor", entries[0].Actor);

        Assert.Equal("Modified", entries[1].Action);
        var compactJson = entries[1].ChangesJson.Replace(" ", string.Empty);
        Assert.Contains("\"property\":\"Name\"", compactJson);
        Assert.Contains("\"old\":\"Felix\"", compactJson);
        Assert.Contains("\"new\":\"Garfield\"", compactJson);
    }

    [Fact]
    public void Delete_records_old_values()
    {
        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s => s.WithChangeLog()));

        var cat = new Cat { Name = "Doomed" };
        db.Cats.Add(cat);
        db.SaveChanges();
        db.Cats.Remove(cat);
        db.SaveChanges();

        var entry = db.ChangeLogEntries.Single(e => e.Action == "Deleted");
        Assert.Contains("Doomed", entry.ChangesJson);
    }
}

public class OutboxSaveChangesInterceptorTests
{
    private record Shipped(int OrderId) : IDomainEvent
    {
        public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
    }

    [Fact]
    public void Events_are_persisted_as_outbox_rows_atomically()
    {
        // The outbox interceptor must NOT call any dispatcher: rows land in the OutboxMessages table.
        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s => s.WithOutbox()));

        var kennel = new Kennel { Id = 11, Title = "Box" };
        kennel.AddDomainEvent(new Shipped(42));
        db.Kennels.Add(kennel);
        db.SaveChanges();

        var row = db.OutboxMessages.Single();
        Assert.Equal(typeof(Shipped).FullName, row.Type);
        Assert.Contains("42", row.PayloadJson);
        Assert.Null(row.ProcessedAtUtc);

        // Aggregates are cleared once the events are durably queued.
        Assert.Empty(kennel.DomainEvents);
    }

    [Fact]
    public void Failed_save_restores_events_to_aggregates()
    {
        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s => s.WithOutbox()));

        // Occupy the primary key so the insert fails at the database level.
        db.Database.ExecuteSqlRaw("INSERT INTO Kennels (Id, Title) VALUES (77, 'Occupied')");

        var kennel = new Kennel { Id = 77, Title = "Conflict" };
        kennel.AddDomainEvent(new Shipped(1));
        db.Kennels.Add(kennel);

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());

        // Outbox rows rolled back with the transaction and events returned to the aggregate.
        Assert.Empty(db.OutboxMessages);
        Assert.Single(kennel.DomainEvents);
    }
}

public class MultiTenancySaveChangesInterceptorTests
{
    [Fact]
    public void Inserts_are_stamped_with_current_tenant()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithMultiTenancy(new StaticTenantProvider("acme"))));

        var pet = new TenantPet { Name = "Rex" };
        db.TenantPets.Add(pet);
        db.SaveChanges();

        Assert.Equal("acme", pet.TenantId);
    }

    [Fact]
    public void Cross_tenant_modification_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.TenantPets.Add(new TenantPet { Id = 5, Name = "Foreign", TenantId = "acme" });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithMultiTenancy(new StaticTenantProvider("globex")))).Options);

        var foreign = ctx.TenantPets.Single(p => p.Id == 5);
        foreign.Name = "Hacked";

        Assert.Throws<CrossTenantAccessException>(() => ctx.SaveChanges());
    }
}

public class MassOperationGuardSaveChangesInterceptorTests
{
    [Fact]
    public void Saves_exceeding_the_limit_are_aborted()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithMassOperationGuard(maxAdded: 4)));

        for (var i = 0; i < 5; i++)
        {
            db.TenantPets.Add(new TenantPet { Name = $"P{i}" });
        }

        Assert.Throws<MassOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public void Saves_within_the_limit_pass()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithMassOperationGuard(maxAdded: 10)));

        for (var i = 0; i < 3; i++)
        {
            db.TenantPets.Add(new TenantPet { Name = $"P{i}" });
        }

        db.SaveChanges();
        Assert.Equal(3, db.TenantPets.Count());
    }
}

public class CachingCommandInterceptorTests
{
    [Fact]
    public void Identical_queries_are_served_from_cache()
    {
        using var database = new SqliteTestDatabase();

        // ONE shared interceptor instance so invalidation affects every context using it.
        var sharedCache = new CachingCommandInterceptor(TimeSpan.FromMinutes(5));

        using (var seed = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.Add(sharedCache))).Options))
        {
            seed.Database.EnsureCreated();
            seed.TenantPets.Add(new TenantPet { Name = "Original" });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.Add(sharedCache))).Options);

        var firstNames = ctx.TenantPets.Select(p => p.Name).ToList();
        Assert.Single(firstNames);
        Assert.Equal(1, sharedCache.Count);   // exactly one cached result set

        // Mutate the database directly behind EF's back.
        ctx.Database.ExecuteSqlRaw("UPDATE TenantPets SET Name = 'Mutated'");

        // Second identical query is served from the buffer: still sees the old value.
        Assert.Equal(firstNames, ctx.TenantPets.Select(p => p.Name).ToList());

        // Invalidate -> the mutation becomes visible again.
        sharedCache.InvalidateAll();
        Assert.Equal(["Mutated"], ctx.TenantPets.Select(p => p.Name).ToList());
        Assert.Empty(ctx.TenantPets.Select(p => p.Name).Except(["Mutated"]));
    }
}

public class NPlusOneDetectorCommandInterceptorTests
{
    [Fact]
    public void Warning_is_raised_when_template_repeats_past_threshold()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        using var ctx = new TestDbContext(database.BuildOptions(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.WithNPlusOneDetection(threshold: 3, loggerFactory: factory));
        }).Options);

        for (var i = 1; i <= 4; i++)
        {
            ctx.TenantPets.Where(p => p.Id == i).ToList();
        }

        var warnings = provider.Records
            .Where(r => r.Level == LogLevel.Warning && r.Message.Contains("N+1"))
            .ToList();

        Assert.Single(warnings);   // exactly once per template
    }
}

public class MetricsCommandInterceptorTests
{
    [Fact]
    public void Command_metrics_are_published()
    {
        const string meterName = "EfCore.Interceptors.Tests";
        var executedCount = 0L;
        var durations = new List<double>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
        {
            if (inst.Name == "ef.command.executed")
            {
                Interlocked.Add(ref executedCount, value);
            }
        });
        listener.SetMeasurementEventCallback<double>((inst, value, _, _) =>
        {
            if (inst.Name == "ef.command.duration")
            {
                lock (durations) durations.Add(value);
            }
        });
        listener.Start();

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithCommandMetrics(meterName)));

        db.Database.EnsureCreated();
        db.TenantPets.Add(new TenantPet { Name = "Measured" });
        db.SaveChanges();
        db.TenantPets.ToList();

        Assert.True(Volatile.Read(ref executedCount) >= 2);
        Assert.NotEmpty(durations);
    }
}

public class WritesRequireTransactionCommandInterceptorTests
{
    [Fact]
    public void Raw_write_outside_transaction_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithTransactionalWrites())).Options);

        Assert.Throws<MissingTransactionException>(
            () => ctx.Database.ExecuteSqlRaw("DELETE FROM TenantPets"));

        // Inside an explicit transaction it passes.
        using (var tx = ctx.Database.BeginTransaction())
        {
            ctx.Database.ExecuteSqlRaw("DELETE FROM TenantPets");
            tx.Rollback();
        }
    }
}

public class ForcedIsolationLevelTransactionInterceptorTests
{
    [Fact]
    public void Transactions_start_at_forced_isolation_level()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithForcedIsolationLevel(IsolationLevel.Serializable)));

        using var tx = db.Database.BeginTransaction();

        Assert.Equal(IsolationLevel.Serializable, tx.GetDbTransaction().IsolationLevel);
    }
}

public class StrictQueryPolicyQueryExpressionInterceptorTests
{
    [Fact]
    public void IgnoreQueryFilters_is_rejected_by_policy()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithStrictQueryPolicy(forbidIgnoreQueryFilters: true))).Options);

        Assert.Throws<QueryPolicyViolationException>(() => ctx.Cats.IgnoreQueryFilters().ToList());

        // Regular queries remain allowed.
        Assert.Empty(ctx.Cats.ToList());
    }
}

public class NullMergeIdentityResolutionInterceptorTests
{
    [Fact]
    public void Only_null_properties_are_filled_from_incoming_instance()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "KeepThis" });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithNullMergingIdentityResolution())).Options);

        var tracked = ctx.Cats.Single(c => c.Name == "KeepThis");
        ctx.Attach(new Cat { Id = tracked.Id, Name = "AttemptOverwrite", CreatedBy = "filler" });

        Assert.Equal("KeepThis", tracked.Name);      // non-null tracked data wins
        Assert.Equal("filler", tracked.CreatedBy);   // null tracked property was filled
    }
}
