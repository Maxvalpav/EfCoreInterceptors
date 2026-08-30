using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Tests;

public class LoadStampingMaterializationInterceptorTests
{
    [Fact]
    public void LoadedAtUtc_is_set_on_materialization()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithLoadStamping()));

        db.Cats.Add(new Cat { Name = "Stamped" });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var loaded = db.Cats.Single();
        Assert.NotNull(loaded.LoadedAtUtc);
    }

    [Fact]
    public void Entities_without_the_interface_are_ignored()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "Plain" });
            seed.SaveChanges();
        }

        // No interceptor registered: LoadedAtUtc stays null even after materialization.
        using var ctx = new TestDbContext(database.BuildOptions().Options);
        Assert.Null(ctx.Cats.Single().LoadedAtUtc);
    }
}

public class IdentityResolutionInterceptorTests
{
    [Fact]
    public void Updating_resolver_merges_incoming_values_instead_of_throwing()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "Original" });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(o =>
            o.AddInterceptors([new UpdatingIdentityResolutionInterceptor()])).Options);

        var tracked = ctx.Cats.Single(c => c.Name == "Original");
        var incoming = new Cat { Id = tracked.Id, Name = "Updated copy" };
        ctx.Attach(incoming);   // throws InvalidOperationException without a resolver

        Assert.Equal("Updated copy", tracked.Name);
    }

    [Fact]
    public void Ignoring_resolver_keeps_tracked_state()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "KeepMe" });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(o =>
            o.AddInterceptors([new IgnoringIdentityResolutionInterceptor()])).Options);

        var tracked = ctx.Cats.Single(c => c.Name == "KeepMe");
        ctx.Attach(new Cat { Id = tracked.Id, Name = "Discarded" });

        Assert.Equal("KeepMe", tracked.Name);
    }

    [Fact]
    public void Custom_overwrite_interceptor_copies_values()
    {
        using var database = new SqliteTestDatabase();
        using (var seed = new TestDbContext(database.BuildOptions().Options))
        {
            seed.Database.EnsureCreated();
            seed.Cats.Add(new Cat { Name = "Source" });
            seed.SaveChanges();
        }

        using var ctx = new TestDbContext(database.BuildOptions(
            o => o.UseEfInterceptors(s => s.WithIdentityResolution(overwriteExisting: true))).Options);

        var tracked = ctx.Cats.Single(c => c.Name == "Source");
        ctx.Attach(new Cat { Id = tracked.Id, Name = "Overwritten" });

        Assert.Equal("Overwritten", tracked.Name);
    }
}
