using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EfCore.Interceptors.Tests;

public class ResilienceExecutionStrategyTests
{
    [Fact]
    public void ShouldRetryOn_detects_transient_messages()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseResilienceExecutionStrategy(maxRetryCount: 3));

        var strategy = db.Database.CreateExecutionStrategy();
        Assert.IsType<ResilienceExecutionStrategy>(strategy);
    }

    [Fact]
    public async Task ExecutionStrategy_retries_on_transient_exception_without_Polly()
    {
        using var database = new SqliteTestDatabase();
        using var db = new TestDbContext(database.BuildOptions(o =>
            o.UseResilienceExecutionStrategy(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(50),
                isTransient: ex => ex.Message.Contains("transient-test"))).Options);

        db.Database.EnsureCreated();

        var strategy = db.Database.CreateExecutionStrategy();
        var attempts = 0;

        await strategy.ExecuteAsync(async (ct) =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new InvalidOperationException("transient-test failure");
            }

            await db.Cats.AddAsync(new Cat { Name = $"ok-{attempts}" }, ct);
            await db.SaveChangesAsync(ct);
        }, CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Single(db.Cats.AsNoTracking());
    }

    [Fact]
    public async Task Custom_isTransient_predicate_is_honored()
    {
        using var database = new SqliteTestDatabase();
        using var db = new TestDbContext(database.BuildOptions(o =>
            o.UseResilienceExecutionStrategy(maxRetryCount: 2, isTransient: ex => ex is TimeoutException)).Options);

        db.Database.EnsureCreated();
        var strategy = db.Database.CreateExecutionStrategy();

        // TimeoutException should retry
        var attempts = 0;
        await strategy.ExecuteAsync(async (ct) =>
        {
            attempts++;
            if (attempts == 1) throw new TimeoutException("simulated");
            await Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(2, attempts);

        // Other exception should NOT retry
        attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await strategy.ExecuteAsync(async (ct) =>
            {
                attempts++;
                throw new InvalidOperationException("permanent failure");
            }, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Provider_specific_ExecutionStrategy_via_factory()
    {
        // Direct factory creation without UseResilienceExecutionStrategy
        using var database = new SqliteTestDatabase();
        using var db = new TestDbContext(database.BuildOptions().Options);
        db.Database.EnsureCreated();

        var deps = db.GetService<Microsoft.EntityFrameworkCore.Storage.ExecutionStrategyDependencies>();
        var factory = ResilienceExecutionStrategyExtensions.CreateResilienceFactory(maxRetryCount: 2);
        var strategy = factory(deps);
        Assert.IsType<ResilienceExecutionStrategy>(strategy);
    }
}
