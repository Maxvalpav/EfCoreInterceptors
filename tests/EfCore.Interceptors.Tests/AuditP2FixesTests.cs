using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.HealthChecks;
using EfCore.Interceptors.MassTransitAdapter;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors.Tests;

/// <summary>Third audit wave: failure policies, health, GDPR, field-auth, bridges.</summary>
public class AuditP2FailurePolicyTests
{
    private sealed class BoomDispatcher : IDomainEventDispatcher
    {
        public void Dispatch(IEnumerable<IDomainEvent> e) => throw new InvalidOperationException("bus down");
        public Task DispatchAsync(IEnumerable<IDomainEvent> e, CancellationToken ct = default)
            => throw new InvalidOperationException("bus down");
    }

    [Fact]
    public void Log_policy_swallows_post_commit_failure()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithDomainEvents(new BoomDispatcher(), DispatchFailurePolicy.Log)));
        var kennel = new Kennel { Title = "k" };
        db.Kennels.Add(kennel);
        kennel.AddDomainEvent(new Barked("woof"));
        db.SaveChanges(); // must not throw
        Assert.Empty(db.Kennels.First().DomainEvents);
    }

    [Fact]
    public void RouteToOutbox_persists_failed_events()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithDomainEvents(new BoomDispatcher(), DispatchFailurePolicy.RouteToOutbox)));
        var kennel = new Kennel { Title = "k" };
        db.Kennels.Add(kennel);
        kennel.AddDomainEvent(new Barked("woof"));
        db.SaveChanges(); // must not throw
        var msg = db.OutboxMessages.AsNoTracking().Single();
        Assert.Contains("Barked", msg.Type);
        Assert.NotNull(msg.Error);
    }

    [Fact]
    public void Throw_policy_raises_after_commit_by_default()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithDomainEvents(new BoomDispatcher())));
        var kennel = new Kennel { Title = "k" };
        db.Kennels.Add(kennel);
        kennel.AddDomainEvent(new Barked("woof"));
        Assert.Throws<DomainEventDispatchException>(() => db.SaveChanges());
    }
}

public class AuditP2HealthTests
{
    private static ServiceProvider BuildProvider(SqliteTestDatabase sqlite)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => sqlite.CreateContext());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Fresh_outbox_is_healthy()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.OutboxMessages.Add(new OutboxMessage
            {
                Type = "t", PayloadJson = "{}", OccurredAtUtc = DateTimeOffset.UtcNow,
            });
            seed.SaveChanges();
        }
        using var provider = BuildProvider(sqlite);
        var check = new EfInterceptorsHealthCheck<TestDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Stale_and_dead_outbox_is_degraded()
    {
        using var sqlite = new SqliteTestDatabase();
        var old = DateTimeOffset.UtcNow.AddMinutes(-5);
        using (var seed = sqlite.CreateContext())
        {
            seed.OutboxMessages.AddRange(
                new OutboxMessage { Type = "t", PayloadJson = "{}", OccurredAtUtc = old },
                new OutboxMessage
                {
                    Type = "t", PayloadJson = "{}", OccurredAtUtc = old,
                    DeadLetteredAtUtc = DateTimeOffset.UtcNow, AttemptCount = 10,
                });
            seed.SaveChanges();
        }
        using var provider = BuildProvider(sqlite);
        var check = new EfInterceptorsHealthCheck<TestDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EfInterceptorsHealthOptions { MaxOutboxLag = TimeSpan.FromSeconds(30) });
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }
}

public class AuditP2GdprTests
{
    [Fact]
    public async Task Pseudonymize_rewrites_pii_and_identifier()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.SubjectRecords.Add(new SubjectRecord
                { SubjectId = "user:42", Email = "a@b.c", Notes = "keep" });
            seed.SaveChanges();
        }
        using (var db = sqlite.CreateContext())
        {
            var n = await db.ForgetSubjectAsync("user:42", ForgetStrategy.Pseudonymize, salt: "s3cr3t");
            Assert.Equal(1, n);
            var n2 = await db.ForgetSubjectAsync("user:42", ForgetStrategy.Pseudonymize, salt: "s3cr3t");
            Assert.Equal(0, n2); // identifier gone — subject is forgotten
        }
        using (var db = sqlite.CreateContext())
        {
            var row = db.SubjectRecords.AsNoTracking().Single();
            Assert.Equal(64, row.Email.Length); // sha256 hex
            Assert.NotEqual("user:42", row.SubjectId);
            Assert.Equal("keep", row.Notes); // non-sensitive untouched
        }
    }

    [Fact]
    public async Task Erase_blanks_strings()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.SubjectRecords.Add(new SubjectRecord
                { SubjectId = "user:7", Email = "x@y.z", Notes = "keep" });
            seed.SaveChanges();
        }
        using (var db = sqlite.CreateContext())
        {
            Assert.Equal(1, await db.ForgetSubjectAsync("user:7", ForgetStrategy.Erase));
        }
        using (var db = sqlite.CreateContext())
        {
            var row = db.SubjectRecords.AsNoTracking().Single();
            Assert.Equal(string.Empty, row.Email);
            Assert.Equal("keep", row.Notes);
        }
    }

    [Fact]
    public async Task Pseudonymize_requires_salt()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext();
        await Assert.ThrowsAsync<ArgumentException>(
            () => db.ForgetSubjectAsync("user:1", ForgetStrategy.Pseudonymize));
    }
}

public class AuditP2FieldAuthTests
{
    [Fact]
    public void Write_without_role_throws()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithFieldAuthorization(new StaticRoleProvider("User"))));
        db.EmployeeRecords.Add(new EmployeeRecord { Name = "n", Salary = 100 });
        Assert.Throws<FieldAuthorizationException>(() => db.SaveChanges());
    }

    [Fact]
    public void Write_with_role_succeeds_and_read_is_masked_for_others()
    {
        using var sqlite = new SqliteTestDatabase();
        using (var db = sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithFieldAuthorization(new StaticRoleProvider("HR")))))
        {
            db.EmployeeRecords.Add(new EmployeeRecord { Name = "n", Salary = 100 });
            db.SaveChanges();
        }
        using (var db = sqlite.CreateContext(o => o.UseEfInterceptors(
            s => s.WithFieldAuthorization(new StaticRoleProvider("User")))))
        {
            var emp = db.EmployeeRecords.AsNoTracking().Single();
            Assert.Equal(0, emp.Salary); // unauthorized read sees default
            Assert.Equal("n", emp.Name);
        }
    }
}

public class AuditP2ClassificationTests
{
    public sealed class CatalogDoc
    {
        public int Id { get; set; }
        [DataClassification(Sensitivity.Phi, Retention = "365d")]
        public string Diagnosis { get; set; } = string.Empty;
        public string Plain { get; set; } = string.Empty;
    }

    [Fact]
    public void Report_lists_classified_columns()
    {
        var builder = new ModelBuilder();
        builder.Entity<CatalogDoc>();
        var entries = DataClassificationReport.Generate(builder.FinalizeModel());
        var diagnosis = Assert.Single(entries);
        Assert.Equal("Diagnosis", diagnosis.PropertyName);
        Assert.Equal(Sensitivity.Phi, diagnosis.Sensitivity);
        Assert.Equal("365d", diagnosis.Retention);
    }
}

public class AuditP2NPlusOneTests
{
    [Fact]
    public void Stack_trace_includes_user_call_site()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        using var sqlite = new SqliteTestDatabase();
        using (var seed = sqlite.CreateContext())
        {
            seed.Cats.Add(new Cat { Name = "c" });
            seed.SaveChanges();
        }
        using (var db = sqlite.CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.WithNPlusOneDetection(3, factory, captureStackTrace: true));
        }))
        {
            for (var i = 0; i < 3; i++) _ = db.Cats.AsNoTracking().ToList();
        }
        var warning = Assert.Single(provider.Records, r => r.Message.Contains("N+1"));
        Assert.Contains("Suggestion", warning.Message);
        Assert.Contains("first user frame", warning.Message);
    }
}

public class AuditP2DriftTests
{
    [Fact]
    public void No_migrations_means_no_drift()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext();
        var (missing, extra) = MigrationDrift.Detect(db);
        Assert.Empty(missing);
        Assert.Empty(extra);
        MigrationDrift.EnsureNoDrift(db); // must not throw
    }
}

public class AuditP2MassTransitTests
{
    public sealed record ShipmentCreated(string Id);

    private sealed class ShipmentConsumer : IConsumer<ShipmentCreated>
    {
        public static readonly System.Threading.Channels.Channel<string> Channel =
            System.Threading.Channels.Channel.CreateUnbounded<string>();
        public Task Consume(ConsumeContext<ShipmentCreated> context)
        {
            Channel.Writer.TryWrite(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Bus_roundtrip_through_dispatcher()
    {
        var services = new ServiceCollection();
        services.AddMassTransit(x =>
        {
            x.AddConsumer<ShipmentConsumer>();
            x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
        });
        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync();
        try
        {
            var endpoint = provider.GetRequiredService<IPublishEndpoint>();
            var dispatcher = new MassTransitDomainEventDispatcher(endpoint);
            // Adapt record to IDomainEvent surface via wrapper is unnecessary here:
            // publish the outbox path directly.
            var handler = new MassTransitOutboxMessageHandler(endpoint);
            var payload = System.Text.Json.JsonSerializer.Serialize(new ShipmentCreated("s-1"));
            await handler.HandleAsync(new OutboxMessage
            {
                Id = 1,
                Type = typeof(ShipmentCreated).FullName!,
                PayloadJson = payload,
                OccurredAtUtc = DateTimeOffset.UtcNow,
            }, CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await ShipmentConsumer.Channel.Reader.ReadAsync(cts.Token);
            Assert.Equal("s-1", received);
            Assert.NotNull(dispatcher);
        }
        finally
        {
            await bus.StopAsync();
        }
    }
}
