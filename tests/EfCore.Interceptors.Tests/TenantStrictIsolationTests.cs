using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Tests;

/// <summary>
/// Security S-2: tenant query filters are bypassable via <c>IgnoreQueryFilters()</c> and raw SQL.
/// <c>WithMultiTenancy()</c> therefore installs a strict query policy by default (explicit opt-out),
/// and raw SQL can be locked behind a <c>TagWith("tenant-safe")</c> allow-list.
/// </summary>
public sealed class TenantStrictIsolationTests
{
    [Fact]
    public void MultiTenancy_forbids_ignore_query_filters_by_default()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithMultiTenancy(new StaticTenantProvider("acme"))));

        Assert.Throws<QueryPolicyViolationException>(() => db.TenantPets.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public void MultiTenancy_policy_opt_out_keeps_ignore_query_filters_working()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithMultiTenancy(new StaticTenantProvider("acme"), strictQueryPolicy: false)));

        // No throw — e.g. admin / recycle-bin contexts.
        Assert.Empty(db.TenantPets.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public void Explicit_policy_is_not_duplicated_by_auto_policy()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s
                .WithStrictQueryPolicy(forbidIgnoreQueryFilters: true)
                .WithMultiTenancy(new StaticTenantProvider("acme"))));

        Assert.Throws<QueryPolicyViolationException>(() => db.TenantPets.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public void Raw_sql_is_allowed_by_default()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s.WithMultiTenancy(new StaticTenantProvider("acme"))));

        // NOTE: unique SQL text per raw-sql test — EF8 shares the compiled-query cache
        // across option variants, so identical SQL would reuse another test's compilation
        // (and its interceptor set). Production builds options once, unaffected.
        Assert.Empty(db.Cats.FromSqlRaw("SELECT * FROM Cats -- allow-by-default").ToList());
    }

    [Fact]
    public void Untagged_raw_sql_is_rejected_in_strict_mode()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s
                .WithMultiTenancy(new StaticTenantProvider("acme"))
                .WithStrictQueryPolicy(forbidIgnoreQueryFilters: true, forbidUntaggedRawSql: true)));

        Assert.Throws<QueryPolicyViolationException>(() => db.Cats.FromSqlRaw("SELECT * FROM Cats -- strict-untagged").ToList());
    }

    [Fact]
    public void Tagged_raw_sql_passes_in_strict_mode()
    {
        using var db = new SqliteTestDatabase().CreateContext(o =>
            o.UseEfInterceptors(s => s
                .WithMultiTenancy(new StaticTenantProvider("acme"))
                .WithStrictQueryPolicy(forbidIgnoreQueryFilters: true, forbidUntaggedRawSql: true)));

        Assert.Empty(db.Cats.FromSqlRaw("SELECT * FROM Cats -- strict-tagged").TagWith("tenant-safe: reviewed report").ToList());
    }
}
