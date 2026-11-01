using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Dashboard;
using EfCore.Interceptors.Entities;
using EfCore.Interceptors.Tests.Infrastructure;

namespace EfCore.Interceptors.Tests;

/// <summary>
/// Regression tests for the 2026-11 working-capability audit (see <c>AUDIT-2026-11.md</c>):
/// quoted schema-qualified table names in the second-level-cache dependency parser and the
/// dashboard dead-letter listing.
/// </summary>
public class Audit2026Q4CacheAndDashboardTests
{
    [Theory]
    // PostgreSQL/Npgsql emits quoted, schema-qualified names: the parser used to return the
    // schema ("public") instead of the table, so table-targeted invalidation evicted everything.
    [InlineData("SELECT \"o\".\"Id\" FROM \"public\".\"Orders\" AS \"o\"", "Orders")]
    [InlineData("SELECT [o].[Id] FROM [dbo].[Orders] AS [o]", "Orders")]
    [InlineData("SELECT * FROM `shop`.`Orders` AS `o`", "Orders")]
    [InlineData("SELECT * FROM \"Orders\" AS \"o\"", "Orders")]
    [InlineData("SELECT * FROM dbo.Orders", "Orders")]
    public void Read_tables_are_parsed_past_schema_qualification(string sql, string expected)
        => Assert.Equal(new[] { expected }, CachingCommandInterceptor.ParseReadTables(sql));

    [Fact]
    public void Write_tables_are_parsed_past_schema_qualification()
    {
        Assert.Equal(new[] { "Orders" },
            CachingCommandInterceptor.ParseWriteTables("UPDATE \"public\".\"Orders\" SET \"Name\" = @p0"));
        Assert.Equal(new[] { "Orders" },
            CachingCommandInterceptor.ParseWriteTables("INSERT INTO \"public\".\"Orders\" (\"Name\") VALUES (@p0)"));
        Assert.Equal(new[] { "Orders" },
            CachingCommandInterceptor.ParseWriteTables("DELETE FROM \"public\".\"Orders\" WHERE \"Id\" = @p0"));
        Assert.Equal(new[] { "Orders" },
            CachingCommandInterceptor.ParseWriteTables("UPDATE OR IGNORE \"Orders\" SET \"Name\" = @p0"));
    }

    [Fact]
    public void Derived_table_and_subquery_targets_are_not_reported_as_tables()
        => Assert.Empty(CachingCommandInterceptor.ParseReadTables("SELECT * FROM (SELECT 1 AS x) AS t"));

    [Fact]
    public async Task Dashboard_dead_letter_page_is_not_hidden_by_newer_pending_rows()
    {
        using var sqlite = new SqliteTestDatabase();
        using var db = sqlite.CreateContext();

        var dead = new OutboxMessage
        {
            Type = "poison",
            PayloadJson = "{}",
            OccurredAtUtc = DateTimeOffset.UtcNow,
            DeadLetteredAtUtc = DateTimeOffset.UtcNow,
        };
        db.OutboxMessages.Add(dead);
        db.SaveChanges();

        for (var i = 0; i < 5; i++)
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Type = "ok",
                PayloadJson = "{}",
                OccurredAtUtc = DateTimeOffset.UtcNow,
            });
        }

        db.SaveChanges();

        // take=2 must still surface the dead-letter: the status filter has to run before Take,
        // otherwise the two newest pending rows hide it forever.
        var deadRows = await DashboardStore.GetOutboxAsync(db, OutboxStatus.Dead, take: 2);
        Assert.Single(deadRows);
        Assert.Equal(dead.Id, deadRows[0].Id);

        var pendingRows = await DashboardStore.GetOutboxAsync(db, OutboxStatus.Pending, take: 2);
        Assert.Equal(2, pendingRows.Count);
        Assert.All(pendingRows, row => Assert.Null(row.DeadLetteredAtUtc));
    }
}
