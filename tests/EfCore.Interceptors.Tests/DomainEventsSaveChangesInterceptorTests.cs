using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Tests;

public class DomainEventsSaveChangesInterceptorTests
{
    [Fact]
    public void Events_are_dispatched_and_cleared_after_successful_save()
    {
        var dispatcher = new RecordingDispatcher();

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithDomainEvents(dispatcher)));

        var kennel = new Kennel { Id = 1, Title = "Woof House" };
        kennel.AddDomainEvent(new Barked("woof"));
        kennel.AddDomainEvent(new Barked("woff"));
        db.Kennels.Add(kennel);
        db.SaveChanges();

        Assert.Equal(2, dispatcher.Dispatched.Count);
        Assert.Empty(kennel.DomainEvents);
    }

    [Fact]
    public void Failed_save_keeps_events_for_the_next_attempt()
    {
        var dispatcher = new RecordingDispatcher();

        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithDomainEvents(dispatcher)));

        // A pre-existing row occupies primary key 7 -> inserting another one fails in the database.
        db.Database.ExecuteSqlRaw("INSERT INTO Kennels (Id, Title) VALUES (7, 'Occupied')");

        var kennel = new Kennel { Id = 7, Title = "Conflict" };
        kennel.AddDomainEvent(new Barked("grr"));
        db.Kennels.Add(kennel);

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());

        // Nothing was dispatched, and the event survived on its aggregate.
        Assert.Empty(dispatcher.Dispatched);
        Assert.Single(kennel.DomainEvents);

        // Resolve the conflict and retry: the queued event is finally published.
        db.Database.ExecuteSqlRaw("DELETE FROM Kennels WHERE Id = 7");
        db.SaveChanges();

        Assert.Single(dispatcher.Dispatched);
        Assert.Empty(kennel.DomainEvents);
    }

    [Fact]
    public void Without_dispatcher_events_are_simply_cleared_after_save()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithDomainEvents()));

        var kennel = new Kennel { Id = 3, Title = "Quiet" };
        kennel.AddDomainEvent(new Barked("..."));
        db.Kennels.Add(kennel);
        db.SaveChanges();

        Assert.Empty(kennel.DomainEvents);
    }
}
