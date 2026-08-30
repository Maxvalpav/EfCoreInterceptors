using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Simple resilience: retries transient failures (e.g. timeout, deadlock) with backoff.
/// Wraps <c>CommandFailed</c> / <c>CommandFailedAsync</c> so actual DB execution failures are retried
/// via EF's execution path rather than dead ReaderExecuting path. No Polly dependency.
/// </summary>
/// <summary>
/// Observes transient command failures and logs them. Real retry is performed by
/// <see cref="ResilienceExecutionStrategy"/> — this interceptor is an observer only.
/// No Polly dependency.
/// </summary>
public class ResilienceCommandInterceptor(
    int maxRetries = 2,
    TimeSpan? baseDelay = null,
    TimeSpan? maxDelay = null,
    ILoggerFactory? loggerFactory = null) : DbCommandInterceptor
{
    // maxRetries/baseDelay/maxDelay kept for API compat (observer) - real retry via ResilienceExecutionStrategy
    private readonly ILogger _logger = loggerFactory?.CreateLogger("EfCore.Interceptors.Resilience") ?? NullLogger.Instance;

    // Suppress unused parameter warnings (API compat)
    private void _KeepApiCompat() => _ = (maxRetries, baseDelay, maxDelay);

    private static readonly System.Text.RegularExpressions.Regex TransientRegex = new(@"\b(timeout|deadlock|transient)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException)
        {
            return true;
        }

        var msg = ex.Message;
        if (msg.Contains("non-transient", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("non_transient", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not transient", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TransientRegex.IsMatch(msg);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        if (IsTransient(eventData.Exception))
        {
            _logger.LogWarning(eventData.Exception, "Transient command failure observed ({Message}). Configure ExecutionStrategy for automatic retries.", eventData.Exception.Message);
        }

        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        // Async path: just observe; real retry should be done via EF ExecutionStrategy.
        // We log transient failures without blocking.
        if (IsTransient(eventData.Exception))
        {
            _logger.LogWarning(eventData.Exception, "Transient async command failure (observed, not retried inside interceptor). Consider configuring ExecutionStrategy for automatic retries.");
        }

        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }
}
