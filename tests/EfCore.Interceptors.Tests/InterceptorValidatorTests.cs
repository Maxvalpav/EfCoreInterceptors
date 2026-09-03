using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Tests;

/// <summary>Startup validator (I-1): silent misconfigurations fail fast instead of leaking data.</summary>
public sealed class InterceptorValidatorTests
{
    private sealed class SoftNoFilterEntity : ISoftDeletableEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
        public DateTimeOffset? DeletedAtUtc { get; set; }
        public string? DeletedBy { get; set; }
    }

    private sealed class NoFilterDb(DbContextOptions<NoFilterDb> options) : DbContext(options)
    {
        public DbSet<SoftNoFilterEntity> Items => Set<SoftNoFilterEntity>();
        public DbSet<ChangeLogEntry> ChangeLogEntries => Set<ChangeLogEntry>();
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    }

    private sealed class BareDb(DbContextOptions<BareDb> options) : DbContext(options)
    {
        public DbSet<SoftNoFilterEntity> Items => Set<SoftNoFilterEntity>();
    }

    private static DbContextOptions<T> SqliteOptions<T>() where T : DbContext
        => new DbContextOptionsBuilder<T>().UseSqlite("DataSource=:memory:").Options;

    [Fact]
    public void Soft_delete_without_filter_is_error()
    {
        var setup = new EfInterceptorsSetup().WithSoftDeletes();
        using var db = new NoFilterDb(SqliteOptions<NoFilterDb>());
        var issues = setup.Validate(db);
        var error = Assert.Single(issues, i => i.Code == "EFI2002");
        Assert.Equal(InterceptorValidationSeverity.Error, error.Severity);
        Assert.Contains("SoftNoFilterEntity", error.Message);
    }

    [Fact]
    public void Soft_delete_with_filter_is_clean()
    {
        var setup = new EfInterceptorsSetup();
        var options = new DbContextOptionsBuilder<FilteredDb>().UseSqlite("DataSource=:memory:").Options;
        using var db = new FilteredDb(options);
        var issues = setup.Validate(db);
        Assert.Empty(issues.Where(i => i.Severity == InterceptorValidationSeverity.Error));
    }

    private sealed class FilteredDb(DbContextOptions<FilteredDb> options) : DbContext(options)
    {
        public DbSet<ChangeLogEntry> ChangeLogEntries => Set<ChangeLogEntry>();
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SoftNoFilterEntity>().HasQueryFilter(e => !e.IsDeleted);
        }
    }

    [Fact]
    public void ChangeLog_without_mapping_is_error()
    {
        var setup = new EfInterceptorsSetup().WithChangeLog();
        using var db = new BareDb(SqliteOptions<BareDb>());
        var issues = setup.Validate(db);
        Assert.Contains(issues, i => i.Code == "EFI2010" && i.Severity == InterceptorValidationSeverity.Error);
    }

    [Fact]
    public void Outbox_without_mapping_is_error()
    {
        var setup = new EfInterceptorsSetup().WithOutbox();
        using var db = new BareDb(SqliteOptions<BareDb>());
        var issues = setup.Validate(db);
        Assert.Contains(issues, i => i.Code == "EFI2011" && i.Severity == InterceptorValidationSeverity.Error);
    }

    [Fact]
    public void Duplicate_interceptors_warn()
    {
        var setup = new EfInterceptorsSetup().WithAuditing().WithAuditing();
        using var db = new BareDb(SqliteOptions<BareDb>());
        var issues = setup.Validate(db);
        var warning = Assert.Single(issues, i => i.Code == "EFI2012");
        Assert.Equal(InterceptorValidationSeverity.Warning, warning.Severity);
    }

    [Fact]
    public void Multitenancy_without_policy_is_info_only()
    {
        var setup = new EfInterceptorsSetup()
            .WithMultiTenancy(new StaticTenantProvider("acme"), strictQueryPolicy: false);
        using var db = new BareDb(SqliteOptions<BareDb>());
        var issues = setup.Validate(db);
        Assert.DoesNotContain(issues, i => i.Severity == InterceptorValidationSeverity.Error);
        Assert.Contains(issues, i => i.Code == "EFI2014" && i.Severity == InterceptorValidationSeverity.Info);
    }

    [Fact]
    public void ValidateAndThrow_throws_on_error()
    {
        var setup = new EfInterceptorsSetup().WithOutbox();
        using var db = new BareDb(SqliteOptions<BareDb>());
        var ex = Assert.Throws<InvalidOperationException>(() => setup.ValidateAndThrow(db));
        Assert.Contains("EFI2011", ex.Message);
    }

    [Fact]
    public void Fully_configured_context_is_clean()
    {
        var setup = new EfInterceptorsSetup()
            .WithSoftDeletes()
            .WithChangeLog()
            .WithOutbox()
            .WithMultiTenancy(new StaticTenantProvider("acme"));
        var options = new DbContextOptionsBuilder<FilteredDb>().UseSqlite("DataSource=:memory:").Options;
        using var db = new FilteredDb(options);
        var issues = setup.Validate(db);
        Assert.Empty(issues.Where(i => i.Severity == InterceptorValidationSeverity.Error));
        Assert.DoesNotContain(issues, i => i.Code == "EFI2012"); // auto-policy is not a duplicate
    }
}
