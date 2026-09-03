using System.Diagnostics;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Delivers one outbox message. Register in DI (scoped is fine — a scope per batch is created).
/// Throw to leave the message unprocessed; it will be retried on the next poll.
/// </summary>
public interface IOutboxMessageHandler
{
    ValueTask HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Background service that polls the <see cref="OutboxMessage"/> table and delivers pending
/// rows through <see cref="IOutboxMessageHandler"/>. Processed rows are stamped with
/// ProcessedAtUtc in their own save; failures are retried with exponential backoff and,
/// after <c>maxAttempts</c>, parked in the dead-letter queue (<see cref="OutboxMessage.DeadLetteredAtUtc"/>).
/// </summary>
public class OutboxProcessor<TContext>(
    IServiceScopeFactory scopeFactory,
    TimeSpan? pollInterval = null,
    int batchSize = 20,
    ILoggerFactory? loggerFactory = null,
    TimeProvider? timeProvider = null,
    int maxAttempts = 10) : BackgroundService where TContext : DbContext
{
    private static readonly ActivitySource Activity = new("EfCore.Interceptors.Outbox");

    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private readonly int _batchSize = Math.Max(1, batchSize);
    private readonly int _maxAttempts = Math.Max(1, maxAttempts);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.Outbox") ?? NullLogger.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox processor started (poll every {Interval:F0}s).",
            _pollInterval.TotalSeconds);

        var idleStreak = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;
            try
            {
                claimed = await ProcessPendingBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox processing cycle failed; retrying after delay.");
                claimed = 0;
            }

            // Adaptive poll (05.3): full batch means backlog — loop immediately;
            // empty batches back off exponentially up to 8x to avoid hot-spinning.
            TimeSpan delay;
            if (claimed >= _batchSize)
            {
                idleStreak = 0;
                delay = TimeSpan.Zero;
            }
            else if (claimed == 0)
            {
                idleStreak++;
                var factor = Math.Min(8, 1 << Math.Min(idleStreak, 3));
                delay = TimeSpan.FromTicks(_pollInterval.Ticks * factor);
            }
            else
            {
                idleStreak = 0;
                delay = _pollInterval;
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Outbox processor stopped.");
    }

    /// <returns>Number of messages claimed in this batch (for adaptive polling).</returns>
    private async Task<int> ProcessPendingBatchAsync(CancellationToken cancellationToken)
    {
        var batchStart = _timeProvider.GetTimestamp();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var handler = scope.ServiceProvider.GetRequiredService<IOutboxMessageHandler>();
        var now = _timeProvider.GetUtcNow();

        // Claim a batch (05.1): the lock-expiry comparison (`LockedUntilUtc < now`
        // on DateTimeOffset) does NOT translate on SQLite/EF10 — and possibly other
        // providers — so it is evaluated client-side over a bounded candidate window.
        // Server side uses equality-only predicates (translatable everywhere); the
        // unique Guid token keeps multi-instance claims disjoint (last-writer-wins:
        // the loser's token is overwritten, so only one worker selects a given row).
        // NOTE: never put `||` / DateTimeOffset comparisons inside ExecuteUpdate here.
        var candidates = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null && m.DeadLetteredAtUtc == null)
            .OrderBy(m => m.Id)
            .Take(_batchSize * 5)
            .Select(m => new { m.Id, m.LockedUntilUtc, m.ClaimToken })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var ids = candidates
            .Where(c => c.ClaimToken == null || c.LockedUntilUtc == null || c.LockedUntilUtc < now)
            .Take(_batchSize)
            .Select(c => c.Id)
            .ToList();

        if (ids.Count == 0)
        {
            SharedMeter.OutboxBatchDuration.Record(_timeProvider.GetElapsedTime(batchStart).TotalSeconds);
            return 0;
        }

        var claimUntil = now.AddMinutes(1);
        var token = Guid.NewGuid();
        await db.Set<OutboxMessage>()
            .Where(m => ids.Contains(m.Id) && m.ProcessedAtUtc == null && m.DeadLetteredAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.LockedUntilUtc, claimUntil)
                .SetProperty(m => m.ClaimToken, (Guid?)token), cancellationToken).ConfigureAwait(false);

        var pending = await db.Set<OutboxMessage>()
            .Where(m => m.ClaimToken == (Guid?)token && m.ProcessedAtUtc == null && m.DeadLetteredAtUtc == null)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        SharedMeter.OutboxClaimed.Add(pending.Count);

        // Lag: age of the oldest message we just claimed (seconds).
        if (pending.Count > 0)
        {
            var oldest = pending.Min(m => m.OccurredAtUtc);
            var lag = (now - oldest).TotalSeconds;
            if (lag > 0) SharedMeter.OutboxLag.Record(lag);
        }

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Optimistic concurrency guard: skip if already processed by another replica
            if (message.ProcessedAtUtc is not null) continue;

            using var activity = Activity.StartActivity("ef.outbox.process", ActivityKind.Internal);
            activity?.SetTag("messaging.system", "efcore-outbox");
            activity?.SetTag("messaging.message.id", message.Id);
            activity?.SetTag("messaging.message.type", message.Type);

            try
            {
                await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);

                // Atomic stamp: only update if still unprocessed (prevents duplicate delivery)
                var affected = await db.Set<OutboxMessage>()
                    .Where(m => m.Id == message.Id && m.ProcessedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.ProcessedAtUtc, _timeProvider.GetUtcNow())
                        .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null)
                        .SetProperty(m => m.ClaimToken, (Guid?)null)
                        .SetProperty(m => m.Error, (string?)null), cancellationToken).ConfigureAwait(false);

                if (affected == 0)
                {
                    _logger.LogWarning("Outbox message {MessageId} was already processed by another worker; skipping.", message.Id);
                    db.Entry(message).State = EntityState.Detached;
                }
                else
                {
                    SharedMeter.OutboxDelivered.Add(1,
                        new KeyValuePair<string, object?>("type", message.Type));
                    _logger.LogDebug("Outbox message {MessageId} ({Type}) delivered.", message.Id, message.Type);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var errorMsg = ex.Message[..Math.Min(1000, ex.Message.Length)];

                if (message.AttemptCount + 1 >= _maxAttempts)
                {
                    // Real dead-letter (05.2): park the poison message so it stops
                    // occupying batch slots and polluting logs.
                    await db.Set<OutboxMessage>()
                        .Where(m => m.Id == message.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.DeadLetteredAtUtc, _timeProvider.GetUtcNow())
                            .SetProperty(m => m.Error, errorMsg)
                            .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null)
                            .SetProperty(m => m.ClaimToken, (Guid?)null)
                            .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1), cancellationToken).ConfigureAwait(false);

                    SharedMeter.OutboxDeadLettered.Add(1,
                        new KeyValuePair<string, object?>("type", message.Type));
                    _logger.LogError(ex, "Outbox message {MessageId} ({Type}) exceeded max retries ({Max}); dead-lettered.",
                        message.Id, message.Type, _maxAttempts);
                }
                else
                {
                    // Exponential backoff with jitter (cap 5 min).
                    var backoffSeconds = Math.Min(300, Math.Pow(2, message.AttemptCount));
                    backoffSeconds *= 0.8 + Random.Shared.NextDouble() * 0.4;
                    var nextLockedUntil = _timeProvider.GetUtcNow().AddSeconds(backoffSeconds);

                    await db.Set<OutboxMessage>()
                        .Where(m => m.Id == message.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                            .SetProperty(m => m.Error, errorMsg)
                            .SetProperty(m => m.ClaimToken, (Guid?)null)
                            .SetProperty(m => m.LockedUntilUtc, nextLockedUntil), cancellationToken).ConfigureAwait(false);

                    SharedMeter.OutboxFailed.Add(1,
                        new KeyValuePair<string, object?>("type", message.Type));
                    _logger.LogError(ex,
                        "Outbox message {MessageId} ({Type}) failed; will retry after {Backoff:F0}s (attempt {Attempt}/{Max}).",
                        message.Id, message.Type, backoffSeconds, message.AttemptCount + 1, _maxAttempts);
                }

                db.Entry(message).State = EntityState.Detached;
            }
        }

        SharedMeter.OutboxBatchDuration.Record(_timeProvider.GetElapsedTime(batchStart).TotalSeconds);
        return pending.Count;
    }
}

public static class OutboxProcessorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox background worker. Also register your
    /// <c>IOutboxMessageHandler</c> implementation (any lifetime).
    /// </summary>
    public static IServiceCollection AddOutboxProcessor<TContext>(
        this IServiceCollection services,
        TimeSpan? pollInterval = null,
        int batchSize = 20,
        TimeProvider? timeProvider = null,
        int maxAttempts = 10)
        where TContext : DbContext
        => services.AddHostedService(sp => new OutboxProcessor<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            pollInterval,
            batchSize,
            sp.GetService<ILoggerFactory>(),
            timeProvider ?? sp.GetService<TimeProvider>(),
            maxAttempts));
}
