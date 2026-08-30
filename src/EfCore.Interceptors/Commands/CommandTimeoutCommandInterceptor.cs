using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Dynamic per-command timeout: a selector decides the CommandTimeout for each statement —
/// e.g. long reports tagged via <c>TagWith("report")</c> get 300s while everything else keeps
/// the context default (and thus fails fast). The selector must be thread-safe.
/// </summary>
public partial class CommandTimeoutCommandInterceptor(
    Func<string, int?> timeoutSelector) : DbCommandInterceptor
{
    private readonly Func<string, int?> _selector = timeoutSelector;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Apply(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Apply(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Apply(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Apply(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Apply(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>Convenience selector: maps leading TagWith comments to timeouts (seconds).</summary>
    public static Func<string, int?> FromTags(IReadOnlyDictionary<string, int> timeoutsByTag)
        => sql =>
        {
            foreach (var match in TagRegex().Matches(sql).Cast<Match>())
            {
                if (timeoutsByTag.TryGetValue(match.Groups[1].Value, out var seconds))
                {
                    return seconds;
                }
            }

            return null;
        };

    protected virtual void Apply(DbCommand command)
    {
        var timeout = _selector(command.CommandText);
        if (timeout is > 0)
        {
            command.CommandTimeout = timeout.Value;
        }
    }

    [GeneratedRegex(@"^--\s*([A-Za-z0-9_\-]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex TagRegex();
}
