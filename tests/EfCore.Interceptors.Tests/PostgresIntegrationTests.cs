using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Observability;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace EfCore.Interceptors.Tests;

/// <summary>
/// Provider integration tests (CI C-2): real PostgreSQL via Testcontainers.
/// Without Docker (or a local PG on localhost:5432) every test skips gracefully —
/// the unit matrix stays green, the <c>integration</c> CI job runs them for real.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        // Fast path first (local PG), slow path (Docker pull/start) second.
        _connectionString = TryLocalPostgres()
            ?? await TryStartContainerAsync().ConfigureAwait(false);
    }

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    private static string LocalCs(string database = "ef_interceptors_pg_test") =>
        $"Host=localhost;Port=5432;Database={database};Username=postgres;Password=postgres;Timeout=3;Command Timeout=10";

    private async Task<string?> TryStartContainerAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithDatabase("ef_interceptors_test")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _container.StartAsync().ConfigureAwait(false);
            return _container.GetConnectionString();
        }
        catch
        {
            if (_container is not null) await _container.DisposeAsync().ConfigureAwait(false);
            _container = null;
            return null; // no Docker — fall back to localhost, then skip
        }
    }

    private static string? TryLocalPostgres()
    {
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(LocalCs());
            conn.Open();
            conn.Close();
            return LocalCs();
        }
        catch
        {
            return null;
        }
    }

    private DbContextOptions<TestDbContext> PgOptions(Action<DbContextOptionsBuilder>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        configure?.Invoke(builder);
        return builder.Options;
    }

    [Fact]
    public async Task Auditing_Works_On_Postgres()
    {
        if (_connectionString is null) return; // skip: no PG available
        await using var db = new TestDbContext(PgOptions(
            o => o.UseEfInterceptors(s => s.WithAuditing(new StaticCurrentUserProvider("pg")))));
        await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);

        var cat = new Cat { Name = "pg-cat" };
        db.Cats.Add(cat);
        await db.SaveChangesAsync().ConfigureAwait(false);

        Assert.NotEqual(default, cat.CreatedAtUtc);
        Assert.Equal("pg-cat", (await db.Cats.AsNoTracking().SingleAsync().ConfigureAwait(false)).Name);
    }

    [Fact]
    public async Task Outbox_Claim_And_Deliver_On_Postgres()
    {
        if (_connectionString is null) return; // skip: no PG available
        await using (var seed = new TestDbContext(PgOptions()))
        {
            await seed.Database.EnsureDeletedAsync().ConfigureAwait(false);
            await seed.Database.EnsureCreatedAsync().ConfigureAwait(false);
            seed.OutboxMessages.Add(new OutboxMessage
            {
                Type = "pg-ok",
                PayloadJson = "{}",
                OccurredAtUtc = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync().ConfigureAwait(false);
        }

        var handler = new RecordingHandler();
        var sp = new ServiceCollection();
        sp.AddScoped(_ => new TestDbContext(PgOptions()));
        sp.AddScoped<IOutboxMessageHandler>(_ => handler);
        var processor = new PgOutboxProcessor(
            sp.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        await processor.RunBatchAsync().ConfigureAwait(false);

        await using var check = new TestDbContext(PgOptions());
        var msg = await check.OutboxMessages.SingleAsync().ConfigureAwait(false);
        Assert.NotNull(msg.ProcessedAtUtc);
        Assert.Null(msg.ClaimToken);
        Assert.Single(handler.Delivered);
    }

    [Fact]
    public async Task Encrypted_Bulk_Update_Roundtrips_On_Postgres()
    {
        if (_connectionString is null) return; // skip: no PG available
        var encryptor = new AesGcmPropertyValueEncryptor(Convert.ToBase64String(new byte[32]));
        await using (var seed = new TestDbContext(PgOptions()))
        {
            await seed.Database.EnsureDeletedAsync().ConfigureAwait(false);
            await seed.Database.EnsureCreatedAsync().ConfigureAwait(false);
            seed.VaultItems.Add(new VaultItem { Name = "a", CardNumber = "plain" });
            await seed.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var db = new TestDbContext(PgOptions()))
        {
            var rows = await db.VaultItems
                .ExecuteEncryptedUpdateAsync(v => v.CardNumber, "rotated", encryptor)
                .ConfigureAwait(false);
            Assert.Equal(1, rows);
        }

        await using var read = new TestDbContext(PgOptions(
            o => o.UseEfInterceptors(s => s.WithPropertyEncryption(encryptor))));
        Assert.Equal("rotated", (await read.VaultItems.AsNoTracking().SingleAsync().ConfigureAwait(false)).CardNumber);
    }

    private sealed class RecordingHandler : IOutboxMessageHandler
    {
        public List<long> Delivered { get; } = [];
        public ValueTask HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Delivered.Add(message.Id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PgOutboxProcessor(IServiceScopeFactory scopeFactory)
        : OutboxProcessor<TestDbContext>(scopeFactory, TimeSpan.FromMilliseconds(10), 20, null, TimeProvider.System, 10)
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
