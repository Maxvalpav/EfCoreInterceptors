using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Logs every EF-generated SQL command with its duration.
/// Start of execution is logged at Debug, completion at Information,
/// cancellation at Information and failures at Error.
/// Parameter values can be included or redacted via <paramref name="includeParameterValues"/>;
/// an optional <paramref name="textRedactor"/> masks sensitive fragments (PII, card numbers)
/// in everything that reaches the log.
/// </summary>
public class SqlLoggingCommandInterceptor(
    ILoggerFactory? loggerFactory = null,
    bool includeParameterValues = false,
    Func<string, string>? textRedactor = null,
    double sampleRate = 1.0) : DbCommandInterceptor
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.Sql") ?? NullLogger.Instance;
    private readonly bool _includeParameterValues = includeParameterValues;
    private readonly Func<string, string>? _redactor = textRedactor;
    private readonly double _sampleRate = Math.Clamp(sampleRate, 0.0, 1.0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        LogExecuting(command, eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        LogExecuting(command, eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        LogExecuted(eventData.Duration, command);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        var reader = await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        LogExecuted(eventData.Duration, command);
        return reader;
    }

    public override int NonQueryExecuted(
        DbCommand command, CommandExecutedEventData eventData, int result)
    {
        LogExecuted(eventData.Duration, command, $"{result} row(s) affected");
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        var affected = await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        LogExecuted(eventData.Duration, command, $"{affected} row(s) affected");
        return affected;
    }

    public override object? ScalarExecuted(
        DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        LogExecuted(eventData.Duration, command, outcome: "scalar");
        return base.ScalarExecuted(command, eventData, result);
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        var value = await base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
        LogExecuted(eventData.Duration, command, outcome: "scalar");
        return value;
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        _logger.LogError(eventData.Exception,
            "EF command FAILED after {Duration:F1}ms. Sql: {Sql}{Parameters}",
            eventData.Duration.TotalMilliseconds, Safe(command.CommandText), Safe(FormatParameters(command)));
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _logger.LogError(eventData.Exception,
            "EF command FAILED after {Duration:F1}ms. Sql: {Sql}{Parameters}",
            eventData.Duration.TotalMilliseconds, Safe(command.CommandText), Safe(FormatParameters(command)));
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
    {
        _logger.LogInformation(
            "EF command CANCELLED after {Duration:F1}ms. Sql: {Sql}",
            eventData.Duration.TotalMilliseconds, Safe(command.CommandText));
        base.CommandCanceled(command, eventData);
    }

    public override Task CommandCanceledAsync(
        DbCommand command, CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "EF command CANCELLED after {Duration:F1}ms. Sql: {Sql}",
            eventData.Duration.TotalMilliseconds, Safe(command.CommandText));
        return base.CommandCanceledAsync(command, eventData, cancellationToken);
    }

    private void LogExecuting(DbCommand command, CommandEventData eventData)
    {
        if (!_logger.IsEnabled(LogLevel.Debug) || !ShouldSample()) return;
        _logger.LogDebug("EF {Method} executing... Sql: {Sql}{Parameters}",
            eventData.ExecuteMethod, Safe(command.CommandText), Safe(FormatParameters(command)));
    }

    private void LogExecuted(TimeSpan duration, DbCommand command, string? outcome = null)
    {
        if (!_logger.IsEnabled(LogLevel.Information) || !ShouldSample()) return;
        _logger.LogInformation("EF command executed in {Duration:F1}ms{Outcome}. Sql: {Sql}",
            duration.TotalMilliseconds,
            outcome is null ? string.Empty : $" ({outcome})",
            Safe(command.CommandText));
    }

    private string Safe(string text) => _redactor?.Invoke(text) ?? text;

    /// <summary>Sampling gate for non-error logs; failures are ALWAYS logged.</summary>
    protected virtual bool ShouldSample()
        => _sampleRate >= 1.0 || SampleDraw() < _sampleRate;

    /// <summary>Overridable for deterministic tests.</summary>
    protected virtual double SampleDraw() => Random.Shared.NextDouble();

    private string FormatParameters(DbCommand command)
    {
        if (command.Parameters.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(" Parameters: [");
        var first = true;
        foreach (var parameter in command.Parameters.OfType<DbParameter>())
        {
            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            sb.Append(parameter.ParameterName);
            if (_includeParameterValues)
            {
                var val = parameter.Value;
                string display;
                if (val is null or DBNull) display = "NULL";
                else if (val is byte[] bytes) display = $"[bytes:{bytes.Length}]";
                else
                {
                    var s = Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    if (s.Length > 256) s = s[..256] + "...";
                    display = s;
                }

                sb.Append('=').Append(Safe(display));
            }
        }

        return sb.Append(']').ToString();
    }
}
