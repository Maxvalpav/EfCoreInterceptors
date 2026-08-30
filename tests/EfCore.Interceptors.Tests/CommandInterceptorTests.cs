using Microsoft.EntityFrameworkCore;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors.Tests;

public class SlowQueryCommandInterceptorTests
{
    [Fact]
    public void Warning_is_logged_when_threshold_exceeded()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        using var db = new SqliteTestDatabase().CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.WithSlowQueryWarning(TimeSpan.Zero, factory));
        });

        db.Cats.Add(new Cat { Name = "Slowpoke" });
        db.SaveChanges();

        Assert.Contains(provider.Records,
            r => r.Level == LogLevel.Warning && r.Message.Contains("Slow EF command detected"));
    }

    [Fact]
    public void No_warning_below_threshold()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        using var db = new SqliteTestDatabase().CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.UseEfInterceptors(s => s.WithSlowQueryWarning(TimeSpan.FromHours(1), factory));
        });

        db.Cats.Add(new Cat { Name = "Speedy" });
        db.SaveChanges();

        Assert.DoesNotContain(provider.Records,
            r => r.Message.Contains("Slow EF command detected"));
    }
}

public class QueryHintsCommandInterceptorTests
{
    [Fact]
    public void Hint_selector_appends_hint_to_executed_sql()
    {
        var provider = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(provider));

        using var db = new SqliteTestDatabase().CreateContext(o =>
        {
            o.UseLoggerFactory(factory);
            o.LogTo((string _) => { }, LogLevel.Debug);
            o.UseEfInterceptors(s => s
                .WithSqlLogging(factory)
                .WithQueryHints(sql => sql.Contains("-- premium", StringComparison.Ordinal)
                    ? "/* PREMIUM PLAN */"
                    : null));
        });

        db.Cats.TagWith("premium").ToList();

        // The SQL logger runs after the hint interceptor, so it sees the appended hint.
        Assert.Contains(provider.Records, r => r.Message.Contains("/* PREMIUM PLAN */"));
    }
}