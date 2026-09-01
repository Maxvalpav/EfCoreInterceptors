using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Connections;

/// <summary>
/// Logs connection lifecycle events (creating/opening/open/closing/closed/fail) and
/// masks secrets such as passwords in the reported connection string.
/// </summary>
public partial class ConnectionLifecycleLoggingInterceptor(
    ILoggerFactory? loggerFactory = null) : DbConnectionInterceptor
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.Connection") ?? NullLogger.Instance;

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Opening {Connection} [{ConnectionString}]...",
                Describe(connection), Mask(connection.ConnectionString));
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Opening {Connection} [{ConnectionString}]...",
                Describe(connection), Mask(connection.ConnectionString));
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        _logger.LogInformation("Opened {Connection} in {Duration:F1}ms.",
            Describe(connection), eventData.Duration.TotalMilliseconds);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ConnectionOpened(connection, eventData);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override InterceptionResult ConnectionClosing(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Closing {Connection}...", Describe(connection));
        return base.ConnectionClosing(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Closing {Connection}...", Describe(connection));
        return base.ConnectionClosingAsync(connection, eventData, result);
    }

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
    {
        _logger.LogInformation("Closed {Connection} after {Duration:F1}ms.",
            Describe(connection), eventData.Duration.TotalMilliseconds);
        base.ConnectionClosed(connection, eventData);
    }

    public override Task ConnectionClosedAsync(
        DbConnection connection, ConnectionEndEventData eventData)
    {
        ConnectionClosed(connection, eventData);
        return base.ConnectionClosedAsync(connection, eventData);
    }

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
    {
        _logger.LogError(eventData.Exception,
            "Connection {Connection} FAILED: {ErrorMessage}",
            Describe(connection), eventData.Exception?.Message);
        base.ConnectionFailed(connection, eventData);
    }

    public override Task ConnectionFailedAsync(
        DbConnection connection, ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ConnectionFailed(connection, eventData);
        return base.ConnectionFailedAsync(connection, eventData, cancellationToken);
    }

    private static string Describe(DbConnection connection)
        => $"{connection.GetType().Name} (Database: '{connection.Database}')";

    internal static string Mask(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return connectionString;
        // Prefer builder-aware masking to handle quoted values with embedded ';' (e.g. Password='a;b') and provider-specific keys
        try
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
            var keysToMask = new List<string>();
            foreach (string key in builder.Keys)
            {
                if (key.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("pwd", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("key", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("auth", StringComparison.OrdinalIgnoreCase))
                    keysToMask.Add(key);
            }
            foreach (var k in keysToMask) builder[k] = "***";
            if (keysToMask.Count > 0) return builder.ConnectionString;
        }
        catch
        {
            // Fallback to regex if builder fails to parse
        }
        return SecretsRegex().Replace(connectionString, "$1=***");
    }

    [GeneratedRegex(@"(?i)\b(password|pwd|secret|token|key|auth)\s*=\s*[^;]*", RegexOptions.None, 100)]
    private static partial Regex SecretsRegex();
}
