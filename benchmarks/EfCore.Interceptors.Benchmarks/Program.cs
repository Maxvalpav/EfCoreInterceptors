using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using EfCore.Interceptors;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

BenchmarkRunner.Run<SaveChangesBenchmarks>();
BenchmarkRunner.Run<QueryBenchmarks>();
return;

public class BenchEntity : IAuditableEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public class BenchSecret
{
    public int Id { get; set; }

    [Encrypted]
    public string? Token { get; set; }
}

public class BenchDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<BenchEntity> Rows => Set<BenchEntity>();
    public DbSet<BenchSecret> Secrets => Set<BenchSecret>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (GetType() == typeof(BenchLogDbContext))
            modelBuilder.Entity<EfCore.Interceptors.Entities.ChangeLogEntry>();
    }
}

public sealed class BenchLogDbContext(DbContextOptions<BenchLogDbContext> options) : BenchDbContext(options);

/// <summary>
/// Per-interceptor SaveChanges matrix (02.3): run and paste the table into README.
/// Answers "what does THIS interceptor cost me" instead of "what does everything cost".
/// </summary>
[MemoryDiagnoser]
public class SaveChangesBenchmarks
{
    private static readonly AesGcmPropertyValueEncryptor Encryptor = new(Convert.ToBase64String(new byte[32]));

    [Benchmark(Baseline = true)]
    public void PlainContext() => InsertOne(new DbContextOptionsBuilder<BenchDbContext>());

    [Benchmark]
    public void PlusAudit() => InsertOne(new DbContextOptionsBuilder<BenchDbContext>()
        .UseEfInterceptors(s => s.WithAuditing()));

    [Benchmark]
    public void PlusAuditSoftDelete() => InsertOne(new DbContextOptionsBuilder<BenchDbContext>()
        .UseEfInterceptors(s => s.WithAuditing().WithSoftDeletes()));

    [Benchmark]
    public void PlusChangeLog() => InsertOne(new DbContextOptionsBuilder<BenchLogDbContext>()
        .UseEfInterceptors(s => s.WithAuditing().WithChangeLog()));

    [Benchmark]
    public void PlusEncryption() => InsertOne(new DbContextOptionsBuilder<BenchDbContext>()
        .UseEfInterceptors(s => s.WithPropertyEncryption(Encryptor)), withSecret: true);

    [Benchmark]
    public void FullSuite() => InsertOne(new DbContextOptionsBuilder<BenchLogDbContext>()
        .UseEfInterceptors(s => s
            .WithAuditing()
            .WithSoftDeletes()
            .WithSlowQueryWarning(TimeSpan.FromSeconds(10))
            .WithLoadStamping()));

    private static void InsertOne<TContext>(DbContextOptionsBuilder<TContext> optionsBuilder, bool withSecret = false)
        where TContext : BenchDbContext
    {
        using var connection = new SqliteConnection("DataSource=:memory:;Pooling=False");
        connection.Open();
        var options = optionsBuilder.UseSqlite(connection).Options;

        using var ctx = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        ctx.Database.EnsureCreated();
        ctx.Rows.Add(new BenchEntity { Name = "x" });
        if (withSecret) ctx.Secrets.Add(new BenchSecret { Token = "s3cr3t" });
        ctx.SaveChanges();
    }
}

/// <summary>SELECT path: uncached vs miss vs hit (02.3).</summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private DbContextOptions<BenchDbContext> _plain = null!;
    private DbContextOptions<BenchDbContext> _cached = null!;
    private SqliteConnection _connection = null!;
    private CachingCommandInterceptor _cache = null!;

    [GlobalSetup]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Pooling=False");
        _connection.Open();
        _cache = new CachingCommandInterceptor(TimeSpan.FromMinutes(5));
        _plain = new DbContextOptionsBuilder<BenchDbContext>().UseSqlite(_connection).Options;
        _cached = new DbContextOptionsBuilder<BenchDbContext>().UseSqlite(_connection)
            .AddInterceptors(_cache).Options;
        using var ctx = new BenchDbContext(_plain);
        ctx.Database.EnsureCreated();
        for (var i = 0; i < 50; i++) ctx.Rows.Add(new BenchEntity { Name = "n" + i });
        ctx.SaveChanges();
    }

    [GlobalCleanup]
    public void Cleanup() => _connection.Dispose();

    [Benchmark(Baseline = true)]
    public int SelectUncached()
    {
        using var ctx = new BenchDbContext(_plain);
        return ctx.Rows.AsNoTracking().ToList().Count;
    }

    [Benchmark]
    public int SelectCacheMiss()
    {
        _cache.InvalidateAll();
        using var ctx = new BenchDbContext(_cached);
        return ctx.Rows.AsNoTracking().ToList().Count;
    }

    [Benchmark]
    public int SelectCacheHit()
    {
        using var ctx = new BenchDbContext(_cached);
        return ctx.Rows.AsNoTracking().ToList().Count;
    }
}
