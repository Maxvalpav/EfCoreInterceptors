using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Tests;

public class SoftDeleteSaveChangesInterceptorTests
{
    [Fact]
    public void Delete_is_converted_into_logical_delete()
    {
        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s =>
            s.WithSoftDeletes(new StaticCurrentUserProvider("admin"))));

        var cat = new Cat { Name = "Murzik" };
        db.Cats.Add(cat);
        db.SaveChanges();

        db.Cats.Remove(cat);
        db.SaveChanges();

        Assert.True(cat.IsDeleted);
        Assert.NotNull(cat.DeletedAtUtc);
        Assert.Equal("admin", cat.DeletedBy);
        Assert.Equal(EntityState.Unchanged, db.Entry(cat).State);

        // The row physically remains in the database.
        db.ChangeTracker.Clear();
        var raw = db.Cats.IgnoreQueryFilters().Single(c => c.Id == cat.Id);
        Assert.True(raw.IsDeleted);

        // ...but is invisible through the global query filter.
        Assert.Empty(db.Cats.Where(c => c.Id == cat.Id));
    }

    [Fact]
    public void Non_deletable_entities_are_untouched()
    {
        using var db = new SqliteTestDatabase().CreateContext(o => o.UseEfInterceptors(s => s.WithSoftDeletes()));

        var cat = new Cat { Name = "Alive" };
        db.Cats.Add(cat);
        db.SaveChanges();
        cat.Name = "Renamed";
        db.SaveChanges();

        Assert.False(cat.IsDeleted);
        Assert.Null(cat.DeletedBy);
    }
}
