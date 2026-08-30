using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using EfCore.Interceptors;
using EfCore.Interceptors.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var summary = BenchmarkRunner.Run<SaveChangesBenchmarks>();
return;

public class BenchEntity : IAuditableEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options)
{
    public DbSet<BenchEntity> Rows => Set<BenchEntity>();
}

[MemoryDiagnoser]
public class SaveChangesBenchmarks
{
    [Benchmark(Baseline = true)]
    public void PlainContext() => InsertOne(new DbContextOptionsBuilder<BenchDbContext>());

    [Benchmark]
    public void WithFullInterceptorSuite() => InsertOne(new DbContextOptionsBuilder<BenchDbContext>()
        .UseEfInterceptors(s => s
            .WithAuditing()
            .WithSoftDeletes()
            .WithSlowQueryWarning(TimeSpan.FromSeconds(10))
            .WithLoadStamping()));

    private static void InsertOne(DbContextOptionsBuilder<BenchDbContext> optionsBuilder)
    {
        using var connection = new SqliteConnection("DataSource=:memory:;Pooling=False");
        connection.Open();
        var options = optionsBuilder.UseSqlite(connection).Options;

        using var ctx = new BenchDbContext(options);
        ctx.Database.EnsureCreated();
        ctx.Rows.Add(new BenchEntity { Name = "x" });
        ctx.SaveChanges();
    }
}
