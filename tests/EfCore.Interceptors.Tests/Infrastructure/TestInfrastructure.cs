using System.ComponentModel.DataAnnotations.Schema;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors.Tests.Infrastructure;

public class Cat : IAuditableEntity, ISoftDeletableEntity, ILoadTimestamped
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }

    public DateTime? LoadedAtUtc { get; set; }
}

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Cat> Cats => Set<Cat>();
    public DbSet<Kennel> Kennels => Set<Kennel>();
    public DbSet<TenantPet> TenantPets => Set<TenantPet>();
    public DbSet<ChangeLogEntry> ChangeLogEntries => Set<ChangeLogEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<VaultItem> VaultItems => Set<VaultItem>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<VersionedDoc> VersionedDocs => Set<VersionedDoc>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Cat>().HasQueryFilter(c => !c.IsDeleted);
        modelBuilder.Entity<VersionedDoc>().Property(d => d.Version).IsConcurrencyToken();
    }
}

public class LedgerEntry : IProtectedEntity
{
    public long Id { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Optimistic-concurrency entity: Version is a mapped concurrency token.</summary>
public class VersionedDoc
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public long Version { get; set; }
}

public class AuditRecord : IImmutableEntity
{
    public long Id { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class VaultItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    [Encrypted]
    public string? CardNumber { get; set; }
}

public class Report : IInitializable
{
    private bool _initialized;

    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;

    [NotMapped]
    public bool LoadedHookRan => _initialized;

    public void OnLoaded() => _initialized = true;
}

/// <summary>
/// The parameterless ctor keeps the model buildable; the interceptor substitutes
/// the constructor binding with a factory at model finalization.
/// </summary>
public class LegacyItem(string legacyTitle)
{
    private LegacyItem() : this("unset") { }

    public int Id { get; set; }
    public string Name { get; set; } = legacyTitle;
}

public class TenantPet : ITenantEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TenantId { get; set; }
}

/// <summary>SQLite in-memory database that lives as long as the test.</summary>
public sealed class SqliteTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestDatabase()
    {
        // Pooling must be disabled: pooled :memory: connections leak schema/data across tests.
        _connection = new SqliteConnection("DataSource=:memory:;Pooling=False");
        _connection.Open();
    }

    public DbContextOptionsBuilder<TestDbContext> BuildOptions(Action<DbContextOptionsBuilder>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection);
        configure?.Invoke(builder);
        return builder;
    }

    /// <summary>Options for a DIFFERENT context type sharing the same in-memory database.</summary>
    public DbContextOptionsBuilder<TContext> BuildOptionsFor<TContext>(
        Action<DbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>()
            .UseSqlite(_connection);
        configure?.Invoke(builder);
        return builder;
    }

    public TestDbContext CreateContext(Action<DbContextOptionsBuilder>? configure = null)
    {
        var context = new TestDbContext(BuildOptions(configure).Options);
        context.Database.EnsureCreated();
        return context;
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Captures log records so tests can assert on interceptor output.</summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message)> Records { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose() { }

    private sealed class Recorder(RecordingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Records.Add((logLevel, category, formatter(state, exception)));
    }
}

public sealed class RecordingDispatcher : IDomainEventDispatcher
{
    public List<IDomainEvent> Dispatched { get; } = [];

    public void Dispatch(IEnumerable<IDomainEvent> domainEvents)
        => Dispatched.AddRange(domainEvents);

    public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        Dispatch(domainEvents);
        return Task.CompletedTask;
    }
}

public class Dog : IAuditableEntity
{
    public int Id { get; set; }
    public string Breed { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public class Kennel : IHasDomainEvents
{
    private readonly List<IDomainEvent> _events = [];

    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    public void AddDomainEvent(IDomainEvent domainEvent) => _events.Add(domainEvent);
    public void ClearDomainEvents() => _events.Clear();
}

public sealed record Barked(string Sound) : IDomainEvent
{
    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
