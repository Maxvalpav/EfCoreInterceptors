using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EfCore.Interceptors.Tests;

// Provider-matrix test — uses real Postgres if available, otherwise skipped (Testcontainers-style)
public class PostgresProviderTests
{
    private const string PgCs = "Host=localhost;Port=5432;Database=ef_interceptors_pg_test;Username=postgres;Password=postgres";

    private static bool CanConnect()
    {
        try { using var conn = new Npgsql.NpgsqlConnection(PgCs); conn.Open(); conn.Close(); return true; } catch { return false; }
    }

    [Fact]
    public async Task Postgres_Auditing_Works()
    {
        if (!CanConnect()) return; // skip if PG not available (Testcontainers-style)
        var opts = new DbContextOptionsBuilder<ProviderTestDb>().UseNpgsql(PgCs).UseEfInterceptors(s => s.WithAuditing(new StaticCurrentUserProvider("pg"))).Options;
        await using var db = new ProviderTestDb(opts);
        await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
        var e = new ProviderEntity { Name = "pg" }; db.Add(e); await db.SaveChangesAsync();
        Assert.NotEqual(default, e.CreatedAtUtc);
    }

    private class ProviderEntity : Abstractions.IAuditableEntity { public int Id{get;set;} public string Name{get;set;}=""; public DateTimeOffset CreatedAtUtc{get;set;} public string? CreatedBy{get;set;} public DateTimeOffset? UpdatedAtUtc{get;set;} public string? UpdatedBy{get;set;}}
    private class ProviderTestDb : DbContext { public ProviderTestDb(DbContextOptions o):base(o){} protected override void OnModelCreating(ModelBuilder b){ b.Entity<ProviderEntity>(); } }
}
