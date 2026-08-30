using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Simple resilience: retries transient failures (e.g. timeout, deadlock) with backoff.
/// No Polly dependency; use for quick wins before introducing full resilience pipeline.
/// </summary>
public class ResilienceCommandInterceptor(
    int maxRetries = 2,
    TimeSpan? baseDelay = null,
    ILoggerFactory? loggerFactory = null) : DbCommandInterceptor
{
    private readonly int _maxRetries = Math.Max(0, maxRetries);
    private readonly TimeSpan _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(100);
    private readonly ILogger _logger = loggerFactory?.CreateLogger("EfCore.Interceptors.Resilience") ?? NullLogger.Instance;

    private bool IsTransient(Exception ex) => ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
        || ex is TimeoutException;

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        for (var i = 0; i <= _maxRetries; i++)
        {
            try { return base.ReaderExecuting(command, eventData, result); }
            catch (Exception ex) when (IsTransient(ex) && i < _maxRetries)
            {
                _logger.LogWarning(ex, "Transient failure, retry {Attempt}/{Max}", i + 1, _maxRetries);
                Thread.Sleep(Backoff(i));
            }
        }
        return base.ReaderExecuting(command, eventData, result);
    }
    private TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
}
