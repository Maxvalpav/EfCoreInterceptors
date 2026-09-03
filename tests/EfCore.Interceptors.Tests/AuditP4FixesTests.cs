using EfCore.Interceptors.Dashboard;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Sagas;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.Tests;

/// <summary>Fifth audit wave: temporal versions, durable sagas, dashboard.</summary>
public class AuditP4TemporalTests
{
    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Versions_and_asof_reconstruction()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(t0);
        using var sqlite = new SqliteTestDatabase();
        Func<TestDbContext> open = () => sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithTemporalTracking(clock: clock)));

        using (var db = open())
        {
            db.TemporalDocs.Add(new TemporalDoc { Title = "v1" });
            db.SaveChanges();
        }
        clock.Now = t0.AddHours(1);
        using (var db = open())
        {
            var doc = db.TemporalDocs.Single();
            doc.Title = "v2";
            db.SaveChanges();
        }
        clock.Now = t0.AddHours(2);
        using (var db = open())
        {
            var doc = db.TemporalDocs.Single();
            db.TemporalDocs.Remove(doc);
            db.SaveChanges();
        }

        using (var db = sqlite.CreateContext())
        {
            var name = typeof(TemporalDoc).FullName!;
            var history = await TemporalQuery.GetHistoryAsync<TemporalDoc>(db, """{"Id":1}""");
            Assert.Equal(3, history.Count); // Added, Modified, Deleted tombstone
            Assert.All(history, r => Assert.Equal(name, r.EntityName));
            Assert.Equal(t0.AddHours(1).UtcTicks, history[0].TicksTo); // closed by modify
            Assert.Equal(t0.AddHours(2).UtcTicks, history[1].TicksTo); // closed by delete
            Assert.Equal("Deleted", history[2].Action);
            Assert.Equal(history[2].TicksFrom, history[2].TicksTo); // tombstone never open
        }

        using (var db = sqlite.CreateContext())
        {
            var past = await TemporalQuery.AsOfAsync<TemporalDoc>(db, t0.AddMinutes(30));
            Assert.Equal("v1", Assert.Single(past).Title);

            var mid = await TemporalQuery.AsOfAsync<TemporalDoc>(db, t0.AddMinutes(90));
            Assert.Equal("v2", Assert.Single(mid).Title);

            var after = await TemporalQuery.AsOfAsync<TemporalDoc>(db, t0.AddHours(3));
            Assert.Empty(after); // deleted — tombstone is never open
        }
    }

    [Fact]
    public async Task Added_keys_are_patched_after_save()
    {
        var t0 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(t0);
        using var sqlite = new SqliteTestDatabase();
        using (var db = sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithTemporalTracking(clock: clock))))
        {
            db.TemporalDocs.Add(new TemporalDoc { Title = "k" });
            db.SaveChanges();
        }
        using (var db = sqlite.CreateContext())
        {
            var record = db.TemporalRecords.AsNoTracking().Single();
            Assert.Contains("\"Id\":1", record.SnapshotJson.Replace(" ", ""));
            var back = await TemporalQuery.AsOfAsync<TemporalDoc>(db, t0.AddMinutes(1));
            Assert.Equal(1, Assert.Single(back).Id);
        }
    }
}

public class AuditP4SagaTests
{
    private static ServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private static SagaDefinition TwoSteps(List<string> log, bool failSecond = false) => new()
    {
        Type = "order",
        Steps =
        [
            new SagaStep
            {
                Name = "reserve",
                Execute = (_, _) => { log.Add("reserve"); return Task.CompletedTask; },
                Compensate = (_, _, _) => { log.Add("unreserve"); return Task.CompletedTask; }
            },
            new SagaStep
            {
                Name = "charge",
                Execute = (_, _) => failSecond
                    ? throw new InvalidOperationException("card declined")
                    : Task.Run(() => log.Add("charge")),
                Compensate = (_, _, _) => { log.Add("refund"); return Task.CompletedTask; }
            }
        ]
    };

    [Fact]
    public async Task Success_completes_all_steps()
    {
        using var sqlite = new SqliteTestDatabase();
        var log = new List<string>();
        using (var db = sqlite.CreateContext())
        {
            var result = await SagaRunner.RunAsync(Services(), db, "saga-1", TwoSteps(log));
            Assert.True(result.Succeeded);
            Assert.Equal(2, result.ExecutedSteps);
        }
        using (var db = sqlite.CreateContext())
        {
            var instance = db.SagaInstances.AsNoTracking().Single(s => s.Id == "saga-1");
            Assert.Equal(SagaState.Completed, instance.State);
            Assert.Equal(2, instance.StepIndex);
        }
        Assert.Equal(["reserve", "charge"], log);
    }

    [Fact]
    public async Task Failure_compensates_in_reverse()
    {
        using var sqlite = new SqliteTestDatabase();
        var log = new List<string>();
        SagaResult result;
        using (var db = sqlite.CreateContext())
        {
            result = await SagaRunner.RunAsync(Services(), db, "saga-2", TwoSteps(log, failSecond: true));
        }
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExecutedSteps);
        Assert.Equal(1, result.CompensatedSteps);
        Assert.Equal(SagaState.Compensated,
            sqlite.CreateContext().SagaInstances.AsNoTracking().Single(s => s.Id == "saga-2").State);
        Assert.Equal(["reserve", "unreserve"], log);
    }

    [Fact]
    public async Task Resume_skips_completed_steps()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.SagaInstances.Add(new SagaInstance
            {
                Id = "saga-3", SagaType = "order", StepIndex = 1,
                State = SagaState.InProgress, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            seed.SaveChanges();
        }
        var log = new List<string>();
        using (var db = sqlite.CreateContext())
        {
            var result = await SagaRunner.RunAsync(Services(), db, "saga-3", TwoSteps(log));
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.ExecutedSteps); // only step 2 ran
        }
        Assert.Equal(["charge"], log);
    }
}

public class AuditP4DashboardTests
{
    [Fact]
    public async Task Store_stats_retry_and_purge()
    {
        using var sqlite = new SqliteTestDatabase();
        var now = DateTimeOffset.UtcNow;
        using (var seed = sqlite.CreateContext())
        {
            seed.OutboxMessages.AddRange(
                new OutboxMessage { Type = "t", PayloadJson = "{}", OccurredAtUtc = now.AddMinutes(-5) },
                new OutboxMessage
                {
                    Type = "t", PayloadJson = "{}", OccurredAtUtc = now.AddMinutes(-5),
                    DeadLetteredAtUtc = now, AttemptCount = 10, Error = "boom"
                },
                new OutboxMessage
                {
                    Type = "t", PayloadJson = "{}", OccurredAtUtc = now.AddDays(-40),
                    ProcessedAtUtc = now.AddDays(-40)
                });
            seed.ChangeLogEntries.Add(new ChangeLogEntry
            {
                EntityName = "E", EntityKey = "1", Action = "Added",
                ChangesJson = "[]", TimestampUtc = now
            });
            seed.SaveChanges();
        }
        using (var db = sqlite.CreateContext())
        {
            var stats = await DashboardStore.GetOutboxStatsAsync(db);
            Assert.Equal(1, stats.Pending);
            Assert.Equal(1, stats.DeadLettered);
            Assert.NotNull(stats.LagSeconds);

            var dead = await DashboardStore.GetOutboxAsync(db, OutboxStatus.Dead);
            var deadId = Assert.Single(dead).Id;
            Assert.True(await DashboardStore.RetryOutboxAsync(db, deadId));
            Assert.False(await DashboardStore.RetryOutboxAsync(db, 123456789));
            Assert.Equal(2, (await DashboardStore.GetOutboxStatsAsync(db)).Pending);

            Assert.Equal(1, await DashboardStore.PurgeDeliveredAsync(db, days: 30));
            Assert.Single(await DashboardStore.GetChangeLogAsync(db));
        }
    }

    [Fact]
    public async Task Http_endpoints_serve_stats_and_html()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.OutboxMessages.Add(new OutboxMessage
                { Type = "t", PayloadJson = "{}", OccurredAtUtc = DateTimeOffset.UtcNow });
            seed.SaveChanges();
        }

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped(_ => sqlite.CreateContext());
        var app = builder.Build();
        app.MapEfInterceptorsDashboard<TestDbContext>("/db-admin");
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            var baseUrl = app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

            var stats = await client.GetStringAsync("/db-admin/api/outbox/stats");
            Assert.Contains("pending", stats, StringComparison.OrdinalIgnoreCase);

            var html = await client.GetStringAsync("/db-admin/");
            Assert.Contains("EfCore.Interceptors", html, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
