using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;

namespace EfCore.Interceptors.Tests;

public class AuditSaveChangesInterceptorTests
{
    [Fact]
    public void Insert_stamps_created_and_updated()
    {
        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s =>
            s.WithAuditing(new StaticCurrentUserProvider("alice"))));

        var cat = new Cat { Name = "Barsik" };
        db.Cats.Add(cat);
        db.SaveChanges();

        Assert.Equal("alice", cat.CreatedBy);
        Assert.Equal("alice", cat.UpdatedBy);
        Assert.Equal(cat.CreatedAtUtc, cat.UpdatedAtUtc);
        Assert.True(cat.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Update_refreshes_updated_but_protects_created()
    {
        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s =>
            s.WithAuditing(new StaticCurrentUserProvider("bob"))));

        var cat = new Cat { Name = "Original" };
        db.Cats.Add(cat);
        db.SaveChanges();
        var createdAt = cat.CreatedAtUtc;

        cat.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-30); // attempt to tamper
        cat.Name = "Changed";
        db.SaveChanges();

        Assert.Equal(createdAt, cat.CreatedAtUtc);             // protected
        Assert.Equal("bob", cat.UpdatedBy);
        Assert.True(cat.UpdatedAtUtc >= createdAt);
    }

    [Fact]
    public void Uses_time_provider_when_configured()
    {
        var fixedTime = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(fixedTime);

        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s => s.WithAuditing(clock: clock)));

        var cat = new Cat { Name = "Frozen" };
        db.Cats.Add(cat);
        db.SaveChanges();

        Assert.Equal(fixedTime, cat.CreatedAtUtc);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}