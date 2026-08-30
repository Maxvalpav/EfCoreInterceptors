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
    ILoggerFactory? loggerFactory = null) : BackgroundService where TContext : DbContext
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    private readonly int _batchSize = Math.Max(1, batchSize);
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
                await ProcessPendingBatchAsync(stoppingToken);
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
                await Task.Delay(_pollInterval, stoppingToken);
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

        var pending = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null)
            .OrderBy(m => m.Id)
            .Take(_batchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await handler.HandleAsync(message, cancellationToken);

                message.ProcessedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                _logger.LogDebug("Outbox message {MessageId} ({Type}) delivered.", message.Id, message.Type);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Poison/failed message: left unprocessed, retried next cycle.
                _logger.LogError(ex,
                    "Outbox message {MessageId} ({Type}) failed; will retry.",
                    message.Id, message.Type);
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
        int batchSize = 20)
        where TContext : DbContext
        => services.AddHostedService(sp => new OutboxProcessor<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            pollInterval,
            batchSize,
            sp.GetService<ILoggerFactory>()));
}
