using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EfCore.Interceptors.HealthChecks;

/// <summary>Thresholds for <see cref="EfInterceptorsHealthCheck{TContext}"/> (03.12).</summary>
public sealed class EfInterceptorsHealthOptions
{
    /// <summary>Oldest undelivered message age before Degraded. Default 30s.</summary>
    public TimeSpan MaxOutboxLag { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Undelivered message count before Degraded. Default 1000.</summary>
    public int MaxPendingOutbox { get; set; } = 1000;

    /// <summary>When true, any dead-lettered message reports Degraded. Default true.</summary>
    public bool DegradeOnDeadLetter { get; set; } = true;

    /// <summary>Injected clock (testability).</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
}

/// <summary>
/// Liveness of the transactional outbox (03.12): pending count, delivery lag and
/// dead-letters. Reports Healthy when the outbox entity is not mapped (nothing to watch).
/// </summary>
/// <typeparam name="TContext">DbContext with (optionally) OutboxMessage mapped.</typeparam>
public sealed class EfInterceptorsHealthCheck<TContext>(
    IServiceScopeFactory scopeFactory,
    EfInterceptorsHealthOptions? options = null) : IHealthCheck where TContext : DbContext
{
    private readonly EfInterceptorsHealthOptions _options = options ?? new();

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        if (db.Model.FindEntityType(typeof(OutboxMessage)) is null)
            return HealthCheckResult.Healthy("Outbox is not mapped; nothing to watch.");

        var now = _options.Clock.GetUtcNow();
        // Equality-only filters: DateTimeOffset comparisons do not translate on SQLite.
        var rows = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null)
            .Select(m => new { m.OccurredAtUtc, m.DeadLetteredAtUtc })
            .AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var pending = rows.Where(r => r.DeadLetteredAtUtc == null).ToList();
        var dead = rows.Count - pending.Count;
        var data = new Dictionary<string, object>
        {
            ["outbox.pending"] = pending.Count,
            ["outbox.dead_lettered"] = dead
        };

        if (_options.DegradeOnDeadLetter && dead > 0)
            return HealthCheckResult.Degraded($"Outbox has {dead} dead-lettered messages.", data: data);

        if (pending.Count > _options.MaxPendingOutbox)
            return HealthCheckResult.Degraded(
                $"Outbox backlog {pending.Count} exceeds {_options.MaxPendingOutbox}.", data: data);

        if (pending.Count > 0)
        {
            var oldest = pending.Min(r => r.OccurredAtUtc);
            var lag = now - oldest;
            data["outbox.lag_seconds"] = lag.TotalSeconds;
            if (lag > _options.MaxOutboxLag)
                return HealthCheckResult.Degraded(
                    $"Outbox lag {lag.TotalSeconds:F0}s exceeds {_options.MaxOutboxLag.TotalSeconds:F0}s.", data: data);
        }

        return HealthCheckResult.Healthy("Outbox is flowing.", data);
    }
}

public static class EfInterceptorsHealthChecksExtensions
{
    /// <summary>
    /// Registers the outbox health probe (03.12):
    /// <c>services.AddHealthChecks().AddEfInterceptorsHealth&lt;AppDbContext&gt;()</c>.
    /// </summary>
    public static IHealthChecksBuilder AddEfInterceptorsHealth<TContext>(
        this IHealthChecksBuilder builder,
        string name = "ef-interceptors",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Action<EfInterceptorsHealthOptions>? configure = null)
        where TContext : DbContext
    {
        var options = new EfInterceptorsHealthOptions();
        configure?.Invoke(options);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new EfInterceptorsHealthCheck<TContext>(
                sp.GetRequiredService<IServiceScopeFactory>(), options),
            failureStatus,
            tags));
    }
}
