using System.Diagnostics;
using System.Diagnostics.Metrics;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Observability;
using EfCore.Interceptors.Queries;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using EfCore.Interceptors.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors.Tests;

public class CommandSourceBlockerTests
{
    [Fact]
    public void Migrations_source_is_blocked_but_normal_commands_pass()
    {
        using var database = new SqliteTestDatabase();
        // EnsureCreated itself runs as CommandSource.Migrations — build schema without the blocker.
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithCommandSourceBlocker())).Options);

        db_insert_and_read(ctx);
    }

    [Fact]
    public void Raw_sql_can_be_blocked_by_source()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithCommandSourceBlocker(CommandSource.ExecuteSqlRaw))).Options);

        Assert.Throws<BlockedCommandSourceException>(
            () => ctx.Database.ExecuteSqlRaw("SELECT 1"));
    }

    private static void db_insert_and_read(TestDbContext ctx)
    {
        ctx.TenantPets.Add(new TenantPet { Name = "Fine" });
        ctx.SaveChanges();

        Assert.Single(ctx.TenantPets);
    }
}

public class RawSqlUsageDetectorTests
{
    [Fact]
    public void Raw_sql_usage_is_reported()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        var detected = new List<(CommandSource Source, string Sql)>();

        using var db = new SqliteTestDatabase().CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.WithRawSqlUsageDetection(factory,
                (source, sql) => detected.Add((source, sql))));
        });

        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("SELECT 1");

        Assert.Contains(provider.Records,
            r => r.Level == LogLevel.Warning && r.Message.Contains("Raw SQL executed"));
        Assert.Single(detected);
        Assert.Equal(CommandSource.ExecuteSqlRaw, detected[0].Source);
    }
}

public class VersionIncrementSaveChangesInterceptorTests
{
    private class Widget : IVersionedEntity
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public long Version { get; set; }
    }

    private class VersionedDbContext(DbContextOptions<VersionedDbContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
    }

    [Fact]
    public void Version_increments_on_every_update()
    {
        using var database = new SqliteTestDatabase();
        using var ctx = new VersionedDbContext(database.BuildOptionsFor<VersionedDbContext>(
            o => o.UseEfInterceptors(s => s.WithVersionCounter())).Options);

        ctx.Database.EnsureCreated();
        var widget = new Widget { Title = "V" };
        ctx.Widgets.Add(widget);
        ctx.SaveChanges();
        Assert.Equal(0, widget.Version);   // insert does not increment

        widget.Title = "V2";
        ctx.SaveChanges();
        Assert.Equal(1, widget.Version);

        widget.Title = "V3";
        ctx.SaveChanges();
        Assert.Equal(2, widget.Version);
    }
}

public class NewestWinsIdentityResolutionInterceptorTests
{
    [Fact]
    public void Newer_updated_at_overwrites_tracked_instance()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "Old", UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithNewestWinsIdentityResolution())).Options);

        var tracked = ctx.Cats.First(c => c.Name == "Old");
        var incoming = new Cat
        {
            Id = tracked.Id,
            Name = "Newer",
            CreatedAtUtc = tracked.CreatedAtUtc,
            CreatedBy = tracked.CreatedBy,
            UpdatedAtUtc = DateTimeOffset.UtcNow // newer
        };
        ctx.Attach(incoming);

        Assert.Equal("Newer", tracked.Name);
    }

    [Fact]
    public void Older_incoming_data_is_discarded()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "Fresh", UpdatedAtUtc = DateTimeOffset.UtcNow });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithNewestWinsIdentityResolution())).Options);

        var tracked = ctx.Cats.First(c => c.Name == "Fresh");
        var incoming = new Cat
        {
            Id = tracked.Id,
            Name = "Stale",
            CreatedAtUtc = tracked.CreatedAtUtc,
            CreatedBy = tracked.CreatedBy,
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-5) // older
        };
        ctx.Attach(incoming);

        Assert.Equal("Fresh", tracked.Name);
    }
}

public class MaterializationMetricsInterceptorTests
{
    [Fact]
    public void Materialized_entities_are_counted()
    {
        var counted = 0L;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "ef.materialization.entities")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref counted, value));
        listener.Start();

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithMaterializationMetrics()));

        db.Database.EnsureCreated();
        db.TenantPets.Add(new TenantPet { Name = "C1" });
        db.TenantPets.Add(new TenantPet { Name = "C2" });
        db.TenantPets.Add(new TenantPet { Name = "C3" });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        db.TenantPets.ToList();

        Assert.True(Volatile.Read(ref counted) >= 3);
    }
}

public class RequireQueryTagsInterceptorTests
{
    /// <summary>
    /// Unique context type per policy: compiled-query cache is keyed by context/model,
    /// and we don't want other tests' untagged shapes to bypass (or be poisoned by) this policy.
    /// </summary>
    private class TagPolicyDbContext(
        DbContextOptions<TagPolicyDbContext> options,
        string[]? requiredTags = null,
        bool requireAny = false) : DbContext(options)
    {
        public DbSet<TenantPet> Pets => Set<TenantPet>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenantPet>().ToTable("TenantPets");
        }

        // Interceptors wired via options in the test.
        internal string[] RequiredTags => requiredTags ?? [];
        internal bool RequireAny => requireAny;
    }

    private static DbContextOptions<TagPolicyDbContext> Options(
        SqliteTestDatabase database, string[]? tags, bool requireAny)
        => database.BuildOptionsFor<TagPolicyDbContext>(
            o => o.UseEfInterceptors(s =>
            {
                if (tags is not null)
                {
                    s.WithRequiredQueryTags(tags);
                }
                else
                {
                    s.WithRequireAnyQueryTag();
                }
            })).Options;

    [Fact]
    public void Missing_required_tag_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        using var ctx = new TagPolicyDbContext(Options(database, ["tenant:acme"], false));

        Assert.Throws<QueryPolicyViolationException>(() => ctx.Pets.ToList());

        // Tagged query passes.
        Assert.Empty(ctx.Pets.TagWith("tenant:acme").ToList());
    }

    [Fact]
    public void Completely_untagged_queries_rejected_when_configured()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        using var ctx = new TagPolicyDbContext(Options(database, null, requireAny: true));

        Assert.Throws<QueryPolicyViolationException>(() => ctx.Pets.ToList());

        Assert.Empty(ctx.Pets.TagWith("anything").ToList());
    }
}

public class SqlLoggingSamplingTests
{
    private sealed class DeterministicSamplingInterceptor(ILoggerFactory? lf, double[] draws)
        : SqlLoggingCommandInterceptor(lf, includeParameterValues: false, textRedactor: null, sampleRate: 0.5)
    {
        private int _index;

        protected override double SampleDraw() =>
            _index < draws.Length ? draws[_index++] : 1.0; // exhausted -> always skip (1.0 >= 0.5)
    }

    [Fact]
    public void Only_sampled_commands_are_logged()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        // First draw 0.1 (< 0.5) -> log the first command; everything after is skipped.
        var interceptor = new DeterministicSamplingInterceptor(factory, [0.1]);

        using var db = new SqliteTestDatabase().CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.Add(interceptor));
        });

        db.Database.EnsureCreated();
        db.TenantPets.Add(new TenantPet { Name = "S" });
        db.SaveChanges();
        db.TenantPets.ToList();

        var infos = provider.Records.Count(r =>
            r.Category == "EfCore.Interceptors.Sql" && r.Level == LogLevel.Information);

        Assert.Equal(1, infos);
    }
}
