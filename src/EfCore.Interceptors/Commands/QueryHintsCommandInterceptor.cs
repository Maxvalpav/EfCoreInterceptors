using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Appends provider-specific query hints to SQL statements selected by a predicate
/// (for example OPTION (RECOMPILE), MAXDOP or FORCE ORDER on SQL Server).
/// The default selector matches the leading <c>TagWith("...")</c> comments EF embeds in the SQL,
/// so callers can write <c>query.TagWith("recompile")</c> and map that tag to a hint.
/// </summary>
public partial class QueryHintsCommandInterceptor(
    Func<string, string?>? hintSelector = null,
    IReadOnlyDictionary<string, string>? hintsByTag = null) : DbCommandInterceptor
{
    private static readonly Regex LeadingTagPattern = TagRegex();

    private readonly Func<string, string?> _hintSelector =
        hintSelector ?? DefaultSelector(hintsByTag ?? new Dictionary<string, string>());

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ApplyHint(command, eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ApplyHint(command, eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        ApplyHint(command, eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        ApplyHint(command, eventData);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ApplyHint(command, eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyHint(command, eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    protected virtual void ApplyHint(DbCommand command, CommandEventData eventData)
    {
        // Only for single-statement queries to avoid breaking EF batching (fix-plan 2.5)
        if (IsBatched(command.CommandText)) return;
        // Prefer tag-based hints only for query commands, not SaveChanges batches
        if (eventData.CommandSource is CommandSource.SaveChanges or CommandSource.Migrations) return;

        var hint = _hintSelector(command.CommandText);
        if (string.IsNullOrWhiteSpace(hint))
        {
            return;
        }

        var trimmed = command.CommandText.TrimEnd();
        command.CommandText = trimmed.EndsWith(";")
            ? $"{trimmed[..^1]} {hint};"
            : $"{trimmed} {hint}";
    }

    private static bool IsBatched(string sql)
    {
        // Simple heuristic: more than one ';' with non-empty statements indicates EF batching with multiple statements
        var count = 0;
        foreach (var c in sql)
            if (c == ';') count++;
        if (count <= 1) return false;
        // Count non-empty statements
        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return statements.Length > 1;
    }

    private static Func<string, string?> DefaultSelector(IReadOnlyDictionary<string, string> hintsByTag)
        => sql =>
        {
            foreach (var match in LeadingTagPattern.Matches(sql).Cast<Match>())
            {
                if (hintsByTag.TryGetValue(match.Groups[1].Value, out var hint))
                {
                    return hint;
                }
            }

            return null;
        };

    [GeneratedRegex(@"^--\s*([A-Za-z0-9_\-]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex TagRegex();
}
