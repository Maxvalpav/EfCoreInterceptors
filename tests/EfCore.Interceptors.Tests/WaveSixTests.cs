using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Saving;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors.Tests;

public class ConcurrencyRetrySaveChangesInterceptorTests
{
    private static (TestDbContext Stale, VersionedDoc Doc) SeedConflict(
        SqliteTestDatabase database,
        Action<DbContextOptionsBuilder>? staleConfigure = null)
    {
        // 1) начальная строка (Version=0)
        int docId;
        using (var init = new TestDbContext(database.BuildOptions().Options))
        {
            init.Database.EnsureCreated();
            var doc = new VersionedDoc { Title = "init" };
            init.VersionedDocs.Add(doc);
            init.SaveChanges();
            docId = doc.Id;
        }

        // 2) «устаревший» клиент читает ДО чужой записи
        var stale = new TestDbContext(database.BuildOptions(staleConfigure).Options);
        var staleDoc = stale.VersionedDocs.First(d => d.Id == docId);

        // 3) другой пользователь пишет первым и двигает токен версии
        using (var other = new TestDbContext(database.BuildOptions().Options))
        {
            var fresh = other.VersionedDocs.First(d => d.Id == docId);
            fresh.Title = "written-by-other";
            other.SaveChanges();
            other.Database.ExecuteSqlRaw(
                "UPDATE VersionedDocs SET Version = Version + 1 WHERE Id = {0}", docId);
        }

        return (stale, staleDoc);
    }

    [Fact]
    public void ClientWins_keeps_client_values_and_retries()
    {
        using var database = new SqliteTestDatabase();
        var (stale, doc) = SeedConflict(database,
            staleConfigure: o => o.UseEfInterceptors(s =>
                s.WithConcurrencyRetry(ConcurrencyRetryPolicy.ClientWins, maxRetries: 3,
                    initialDelay: TimeSpan.FromMilliseconds(10))));

        doc.Title = "client-change";
        stale.SaveChanges();   // конфликт разрешается автоматически

        Assert.Equal("client-change", doc.Title);
        Assert.Equal("client-change", stale.VersionedDocs.AsNoTracking().Single(d => d.Id == doc.Id).Title);
    }

    [Fact]
    public void StoreWins_discards_client_values()
    {
        using var database = new SqliteTestDatabase();
        var (stale, doc) = SeedConflict(database,
            staleConfigure: o => o.UseEfInterceptors(s =>
                s.WithConcurrencyRetry(ConcurrencyRetryPolicy.StoreWins, maxRetries: 3,
                    initialDelay: TimeSpan.FromMilliseconds(10))));

        doc.Title = "client-change";
        stale.SaveChanges();   // Reload() затирает клиентские изменения значениями из БД

        Assert.Equal("written-by-other", doc.Title);
    }

    [Fact]
    public void Exhausted_retries_surface_the_original_conflict()
    {
        using var database = new SqliteTestDatabase();
        var (stale, doc) = SeedConflict(database,
            staleConfigure: o => o.UseEfInterceptors(s =>
                s.WithConcurrencyRetry(ConcurrencyRetryPolicy.ClientWins, maxRetries: 0)));

        doc.Title = "client-change";
        Assert.Throws<DbUpdateConcurrencyException>(() => stale.SaveChanges());

        // Счётчик попыток очищен: следующий конфликт снова обрабатывается с нуля.
        stale.ChangeTracker.Clear();
        var fresh = stale.VersionedDocs.First();
        fresh.Title = "second-save-ok";
        stale.SaveChanges();
        Assert.Equal("second-save-ok", fresh.Title);
    }
}

public class CustomValidationSaveChangesInterceptorTests
{
    private sealed class RejectBadNames : IEntityValidator
    {
        public IEnumerable<string> Validate(object entity)
            => entity is Cat { Name: "bad" }
                ? ["Name 'bad' is not allowed by business rule."]
                : Enumerable.Empty<string>();
    }

    [Fact]
    public void External_validator_failures_abort_the_save()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithCustomValidation(new RejectBadNames())));

        db.Database.EnsureCreated();
        db.Cats.Add(new Cat { Name = "bad" });

        var failure = Assert.Throws<EntityValidationException>(() => db.SaveChanges());
        Assert.Contains("business rule", failure.Failures.Single().Value.Single());
    }

    [Fact]
    public void Valid_entities_pass_through()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithCustomValidation(new RejectBadNames())));

        db.Database.EnsureCreated();
        db.Cats.Add(new Cat { Name = "good" });
        db.SaveChanges();

        Assert.Single(db.Cats);
    }
}

public class CommandsPerSaveDiagnosticInterceptorTests
{
    private static RecordingLoggerProvider Run(int warnAbove)
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        using var db = new SqliteTestDatabase().CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.WithCommandsPerSaveDiagnostics(warnAbove, factory));
        });

        db.Database.EnsureCreated();
        db.TenantPets.Add(new TenantPet { Name = "P" });
        db.SaveChanges();

        return provider;
    }

    [Fact]
    public void Warning_appears_when_threshold_exceeded()
    {
        var provider = Run(warnAbove: 0);   // любая команда превысит порог
        Assert.Contains(provider.Records,
            r => r.Level == LogLevel.Warning && r.Message.Contains("commands"));
    }

    [Fact]
    public void No_warning_below_threshold()
    {
        var provider = Run(warnAbove: 100);
        Assert.DoesNotContain(provider.Records,
            r => r.Message.Contains("SaveChanges issued"));
    }
}
