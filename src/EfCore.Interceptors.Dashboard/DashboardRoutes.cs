using EfCore.Interceptors.Commands;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.Dashboard;

/// <summary>
/// Opt-in web dashboard (03.13, Hangfire-style): live outbox explorer with dead-letter
/// retry, purge and changelog viewer. Mount with
/// <c>app.MapEfInterceptorsDashboard&lt;AppDbContext&gt;("/db-admin")</c>.
/// No auth is applied — protect with <c>RequireAuthorization()</c> on the returned group.
/// </summary>
public static class DashboardRoutes
{
    /// <typeparam name="TContext">DbContext with OutboxMessage (optionally ChangeLogEntry) mapped.</typeparam>
    /// <param name="endpoints">Usually the WebApplication itself.</param>
    /// <param name="pattern">Mount prefix, e.g. "/db-admin".</param>
    /// <param name="cache">Optional shared L2-cache interceptor for the cache panel.</param>
    public static RouteGroupBuilder MapEfInterceptorsDashboard<TContext>(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/db-admin",
        CachingCommandInterceptor? cache = null) where TContext : DbContext
    {
        var group = endpoints.MapGroup(pattern);

        group.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html"));

        group.MapGet("/api/outbox/stats", async (IServiceProvider sp, CancellationToken ct) =>
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            return Results.Ok(await DashboardStore.GetOutboxStatsAsync(db, ct: ct));
        });

        group.MapGet("/api/outbox", async (
            IServiceProvider sp, string? status, int? take, CancellationToken ct) =>
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var parsed = Enum.TryParse<OutboxStatus>(status, ignoreCase: true, out var s)
                ? s : OutboxStatus.Pending;
            return Results.Ok(await DashboardStore.GetOutboxAsync(db, parsed, take ?? 50, ct));
        });

        group.MapPost("/api/outbox/{id:long}/retry", async (
            IServiceProvider sp, long id, CancellationToken ct) =>
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            return await DashboardStore.RetryOutboxAsync(db, id, ct)
                ? Results.Ok(new { retried = id })
                : Results.NotFound();
        });

        group.MapPost("/api/outbox/purge", async (
            IServiceProvider sp, int? days, CancellationToken ct) =>
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var deleted = await DashboardStore.PurgeDeliveredAsync(db, days ?? 30, ct: ct);
            return Results.Ok(new { purged = deleted });
        });

        group.MapGet("/api/changelog", async (IServiceProvider sp, int? take, CancellationToken ct) =>
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            return Results.Ok(await DashboardStore.GetChangeLogAsync(db, take ?? 50, ct));
        });

        group.MapGet("/api/cache", () => cache is null
            ? Results.Ok(new { enabled = false })
            : Results.Ok(new { enabled = true, entries = cache.Count }));

        return group;
    }
}

file static class DashboardPage
{
    internal const string Html = """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>EfCore.Interceptors dashboard</title>
        <style>body{font-family:system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem}
        table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:.4rem;text-align:left}
        .cards{display:flex;gap:1rem;margin:1rem 0}.card{border:1px solid #ddd;border-radius:8px;padding:1rem;flex:1}
        .card b{font-size:1.6rem}button{cursor:pointer}</style></head>
        <body><h1>EfCore.Interceptors</h1>
        <div class="cards" id="stats"></div>
        <h2>Outbox <select id="status"><option>Pending</option><option>Dead</option><option>All</option></select>
        <button onclick="load()">Reload</button></h2>
        <table><thead><tr><th>Id</th><th>Type</th><th>Attempts</th><th>Error</th><th></th></tr></thead>
        <tbody id="rows"></tbody></table>
        <h2>ChangeLog (recent)</h2>
        <table><thead><tr><th>Id</th><th>Entity</th><th>Key</th><th>Action</th><th>Actor</th></tr></thead>
        <tbody id="log"></tbody></table>
        <script>
        const base = location.pathname.replace(/\/$/, '');
        async function load(){
          const s = await (await fetch(base + '/api/outbox/stats')).json();
          document.getElementById('stats').innerHTML =
            `<div class="card">Pending<br><b>${s.pending}</b></div>` +
            `<div class="card">Dead-letter<br><b>${s.deadLettered}</b></div>` +
            `<div class="card">Lag (s)<br><b>${s.lagSeconds ?? '-'}</b></div>`;
          const st = document.getElementById('status').value;
          const rows = await (await fetch(base + '/api/outbox?status=' + st + '&take=100')).json();
          document.getElementById('rows').innerHTML = rows.map(m =>
            `<tr><td>${m.id}</td><td>${m.type}</td><td>${m.attemptCount}</td>` +
            `<td>${(m.error ?? '').substring(0,120)}</td>` +
            `<td>${m.deadLetteredAtUtc ? `<button onclick="retry(${m.id})">Retry</button>` : ''}</td></tr>`).join('');
          const log = await (await fetch(base + '/api/changelog?take=50')).json();
          document.getElementById('log').innerHTML = log.map(e =>
            `<tr><td>${e.id}</td><td>${e.entityName}</td><td>${e.entityKey}</td><td>${e.action}</td><td>${e.actor ?? ''}</td></tr>`).join('');
        }
        async function retry(id){
          await fetch(base + '/api/outbox/' + id + '/retry', {method:'POST'});
          load();
        }
        document.getElementById('status').onchange = load;
        load();
        </script></body></html>
        """;
}
