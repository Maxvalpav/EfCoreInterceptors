using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Tests;

public class ReadOnlyGuardCommandInterceptorTests
{
    [Fact]
    public void Write_commands_are_blocked()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        using var guarded = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithReadOnlyGuard())).Options);

        // EF wraps the interception failure into DbUpdateException; the guard exception is its cause.
        var failure = Assert.Throws<DbUpdateException>(() =>
        {
            guarded.Cats.Add(new Cat { Name = "Nope" });
            guarded.SaveChanges();
        });

        Assert.IsType<ReadOnlyContextException>(failure.InnerException);
    }

    [Fact]
    public void Reads_are_still_allowed()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "Reader" });
            seed.SaveChanges();
        }

        using var guarded = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithReadOnlyGuard())).Options);

        Assert.Single(guarded.Cats);
    }

    [Fact]
    public void Guard_can_be_enabled_selectively_per_context()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
        }

        // Guard armed only when the predicate matches the context type.
        using var allowed = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithReadOnlyGuard(ctx => ctx is ReportingContext))).Options);

        allowed.Cats.Add(new Cat { Name = "Fine" });
        allowed.SaveChanges();   // AppDbContext is not blocked

        Assert.NotEmpty(allowed.Cats);
    }
}

public class ReportingContext(DbContextOptions<ReportingContext> options) : DbContext(options)
{
    public DbSet<Cat> Cats => Set<Cat>();
}
