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
/// ProcessedAtUtc in their own save; failures are logged and retried on the next poll.
/// </summary>
public class OutboxProcessor<TContext>(
    IServiceScopeFactory scopeFactory,
    TimeSpan? pollInterval = null,
    int batchSize = 20,
    ILoggerFactory? loggerFactory = null,
    TimeProvider? timeProvider = null) : BackgroundService where TContext : DbContext
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private readonly int _batchSize = Math.Max(1, batchSize);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.Outbox") ?? NullLogger.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox processor started (poll every {Interval:F0}s).",
            _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox processing cycle failed; retrying after delay.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Outbox processor stopped.");
    }

    private async Task ProcessPendingBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var handler = scope.ServiceProvider.GetRequiredService<IOutboxMessageHandler>();
        var now = _timeProvider.GetUtcNow();

        // Claim a batch with optimistic lock to avoid double-delivery on multiple instances
        var claimUntil = now.AddMinutes(1);
        await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null && (m.LockedUntilUtc == null || m.LockedUntilUtc < now))
            .OrderBy(m => m.Id)
            .Take(_batchSize)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntilUtc, claimUntil), cancellationToken).ConfigureAwait(false);

        var pending = await db.Set<OutboxMessage>()
            .Where(m => m.LockedUntilUtc == claimUntil && m.ProcessedAtUtc == null)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Optimistic concurrency guard: skip if already processed by another replica
            if (message.ProcessedAtUtc is not null) continue;

            try
            {
                await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);

                // Atomic stamp: only update if still unprocessed (prevents duplicate delivery)
                var affected = await db.Set<OutboxMessage>()
                    .Where(m => m.Id == message.Id && m.ProcessedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.ProcessedAtUtc, _timeProvider.GetUtcNow())
                        .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null)
                        .SetProperty(m => m.Error, (string?)null), cancellationToken).ConfigureAwait(false);

                if (affected == 0)
                {
                    _logger.LogWarning("Outbox message {MessageId} was already processed by another worker; skipping.", message.Id);
                    db.Entry(message).State = EntityState.Detached;
                }
                else
                {
                    _logger.LogDebug("Outbox message {MessageId} ({Type}) delivered.", message.Id, message.Type);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Poison/failed message: increment attempt, store error, apply exponential backoff dead-letter after 10 tries
                var backoff = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, message.AttemptCount)));
                var nextLockedUntil = _timeProvider.GetUtcNow().Add(backoff);
                var errorMsg = ex.Message[..Math.Min(1000, ex.Message.Length)];

                if (message.AttemptCount >= 10)
                {
                    _logger.LogError(ex, "Outbox message {MessageId} ({Type}) exceeded max retries; dead-lettered.", message.Id, message.Type);
                }

                await db.Set<OutboxMessage>()
                    .Where(m => m.Id == message.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                        .SetProperty(m => m.Error, errorMsg)
                        .SetProperty(m => m.LockedUntilUtc, nextLockedUntil), cancellationToken).ConfigureAwait(false);

                db.Entry(message).State = EntityState.Detached;
                _logger.LogError(ex,
                    "Outbox message {MessageId} ({Type}) failed; will retry after {Backoff}s (attempt {Attempt}).",
                    message.Id, message.Type, backoff.TotalSeconds, message.AttemptCount + 1);
            }
        }
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
        TimeProvider? timeProvider = null)
        where TContext : DbContext
        => services.AddHostedService(sp => new OutboxProcessor<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            pollInterval,
            batchSize,
            sp.GetService<ILoggerFactory>(),
            timeProvider ?? sp.GetService<TimeProvider>()));
}
