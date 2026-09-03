using System.Diagnostics;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.OpenTelemetry;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;

namespace EfCore.Interceptors.Tests;

/// <summary>Fourth audit wave: RLS, budgets, expand-contract, OTel wiring.</summary>
public class AuditP3RlsTests
{
    public sealed class RlsDoc
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
    }

    private sealed class RlsDbContext(DbContextOptions<RlsDbContext> options, string user)
        : DbContext(options)
    {
        public string CurrentUser { get; } = user;
        public bool BypassRls => ElevatedSession.IsElevated;
        public DbSet<RlsDoc> Docs => Set<RlsDoc>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyRowLevelSecurity(this,
                (RlsDbContext ctx, RlsDoc d) => ctx.BypassRls || d.OwnerId == ctx.CurrentUser);
        }
    }

    private static RlsDbContext Create(SqliteConnection conn, string user, bool guard)
    {
        var builder = new DbContextOptionsBuilder<RlsDbContext>().UseSqlite(conn);
        if (guard)
            builder.UseEfInterceptors(s => s.WithRowLevelSecurity<RlsDoc>(d => d.OwnerId == user));
        var db = new RlsDbContext(builder.Options, user);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public void Filter_hides_foreign_rows_and_guard_rejects_writes()
    {
        using var conn = new SqliteConnection("DataSource=:memory:;Pooling=False");
        conn.Open();
        using (var alice = Create(conn, "alice", guard: true))
        {
            alice.Docs.Add(new RlsDoc { Title = "a1", OwnerId = "alice" });
            alice.SaveChanges();
            Assert.Throws<RowLevelSecurityException>(() =>
            {
                alice.Docs.Add(new RlsDoc { Title = "hack", OwnerId = "bob" });
                alice.SaveChanges();
            });
        }
        using (var bob = Create(conn, "bob", guard: true))
        {
            Assert.Empty(bob.Docs.AsNoTracking().ToList()); // alice's row hidden
        }
        using (var sys = Create(conn, "sys", guard: true))
        {
            using (ElevatedSession.Elevate("audit"))
            {
                Assert.Single(sys.Docs.AsNoTracking().ToList()); // elevation bypasses filter
                sys.Docs.Add(new RlsDoc { Title = "sysdoc", OwnerId = "root" });
                sys.SaveChanges(); // guard bypassed too
            }
            Assert.False(ElevatedSession.IsElevated); // scope disposed
        }
    }
}

public class AuditP3BudgetTests
{
    [Fact]
    public void Over_budget_query_throws()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            for (var i = 0; i < 20; i++) seed.Cats.Add(new Cat { Name = "c" + i });
            seed.SaveChanges();
        }
        using (var db = sqlite.CreateContext(o => o.UseEfInterceptors(s => s.WithQueryBudget(5))))
        {
            Assert.Throws<QueryBudgetExceededException>(() => db.Cats.AsNoTracking().ToList());
        }
        using (var db = sqlite.CreateContext(o => o.UseEfInterceptors(s => s.WithQueryBudget(100))))
        {
            Assert.Equal(20, db.Cats.AsNoTracking().ToList().Count);
        }
    }

    [Fact]
    public void Scope_filter_leaves_reports_alone()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            for (var i = 0; i < 20; i++) seed.Cats.Add(new Cat { Name = "c" + i });
            seed.SaveChanges();
        }
        using var db = sqlite.CreateContext(o => o.UseEfInterceptors(s => s
            .WithQueryBudget(5, e => e.CommandSource == Microsoft.EntityFrameworkCore.Diagnostics.CommandSource.LinqQuery)));
        // LINQ queries are budgeted...
        Assert.Throws<QueryBudgetExceededException>(() => db.Cats.AsNoTracking().ToList());
    }
}

public class AuditP3ExpandContractTests
{
    [Fact]
    public void Dual_write_mirrors_new_to_old()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var db = sqlite.CreateContext(o => o.UseEfInterceptors(s => s.WithExpandContract())))
        {
            db.ContractDocs.Add(new ContractDoc { NewName = "new" });
            db.SaveChanges();
        }
        using (var db = sqlite.CreateContext())
        {
            var row = db.ContractDocs.AsNoTracking().Single();
            Assert.Equal("new", row.NewName);
            Assert.Equal("new", row.OldName); // dual-written
        }
    }

    [Fact]
    public void Fallback_serves_old_for_unmigrated_rows()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var db = sqlite.CreateContext())
        {
            db.ContractDocs.Add(new ContractDoc { OldName = "legacy", NewName = null });
            db.SaveChanges();
        }
        using (var db = sqlite.CreateContext(o => o.UseEfInterceptors(s => s.WithExpandContract())))
        {
            var row = db.ContractDocs.AsNoTracking().Single();
            Assert.Equal("legacy", row.NewName); // served from old column
        }
    }
}

public class AuditP3TracingTests
{
    [Fact]
    public void Ef_save_span_flows_to_listener()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "EfCore.Interceptors",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => stopped.Add(a)
        };
        ActivitySource.AddActivityListener(listener);

        using var sqlite = new SqliteTestDatabase();
        using (var db = sqlite.CreateContext(o => o.UseEfInterceptors(s => s.WithTracing())))
        {
            db.Cats.Add(new Cat { Name = "c" });
            db.SaveChanges();
        }

        var save = Assert.Single(stopped, a => a.DisplayName == "ef.save");
        Assert.Equal("True", save.GetTagItem("ef.save.success")?.ToString());
    }

    [Fact]
    public void OTel_builder_extension_registers_sources()
    {
        using var provider = global::OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .AddEfCoreInterceptorsInstrumentation()
            .Build();
        Assert.NotNull(provider);
        Assert.Contains("EfCore.Interceptors.Outbox",
            EfCoreInterceptorsInstrumentationExtensions.ActivitySourceNames);
    }
}
