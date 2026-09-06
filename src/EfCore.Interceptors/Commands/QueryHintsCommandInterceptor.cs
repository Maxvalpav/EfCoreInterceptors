using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Appends provider-specific query hints to SQL statements selected by a predicate
/// (for example OPTION (RECOMPILE), MAXDOP or FORCE ORDER on SQL Server).
/// The default selector matches the leading <c>TagWith("...")</c> comments EF embeds in the SQL,
/// so callers can write <c>query.TagWith("recompile")</c> and map that tag to a hint.
/// </summary>
/// <remarks>
/// Provider guard (audit §3.3): a hint valid on one dialect (e.g. <c>OPTION (RECOMPILE)</c>
/// on SQL Server) is a syntax error on another. Pass <paramref name="allowedProviders"/>
/// (EF provider-name fragments, e.g. <c>"SqlServer"</c>) or a custom
/// <paramref name="canApply"/> predicate — on mismatch the hint is skipped and a
/// diagnostic is logged instead of breaking the query. Use
/// <see cref="ForProvider"/> / <see cref="ForSqlServer"/> / <see cref="ForPostgres"/>
/// factories for ready-made guards.
/// </remarks>
public partial class QueryHintsCommandInterceptor(
    Func<string, string?>? hintSelector = null,
    IReadOnlyDictionary<string, string>? hintsByTag = null,
    IReadOnlySet<string>? allowedProviders = null,
    Func<CommandEventData, bool>? canApply = null,
    ILoggerFactory? loggerFactory = null) : DbCommandInterceptor
{
    private static readonly Regex LeadingTagPattern = TagRegex();

    private readonly Func<string, string?> _hintSelector =
        hintSelector ?? DefaultSelector(hintsByTag ?? new Dictionary<string, string>());
    private readonly IReadOnlySet<string>? _allowedProviders = allowedProviders;
    private readonly Func<CommandEventData, bool>? _canApply = canApply;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.QueryHints") ?? NullLogger.Instance;

    /// <summary>Creates a hint interceptor guarded to a single provider fragment.</summary>
    public static QueryHintsCommandInterceptor ForProvider(
        string providerFragment,
        IReadOnlyDictionary<string, string> hintsByTag,
        ILoggerFactory? loggerFactory = null)
        => new(hintsByTag: hintsByTag,
            allowedProviders: new HashSet<string>([providerFragment], StringComparer.OrdinalIgnoreCase),
            loggerFactory: loggerFactory);

    /// <summary>SQL Server hints (OPTION ..., MAXDOP); skipped on any other provider.</summary>
    public static QueryHintsCommandInterceptor ForSqlServer(
        IReadOnlyDictionary<string, string>? hintsByTag = null,
        ILoggerFactory? loggerFactory = null)
        => ForProvider("SqlServer",
            hintsByTag ?? Queries.SqlServerQueryHints.Defaults, loggerFactory);

    /// <summary>PostgreSQL locking hints (FOR UPDATE/SHARE); skipped on any other provider.</summary>
    public static QueryHintsCommandInterceptor ForPostgres(
        IReadOnlyDictionary<string, string>? hintsByTag = null,
        ILoggerFactory? loggerFactory = null)
        => ForProvider("Npgsql",
            hintsByTag ?? Queries.PostgresQueryHints.Defaults, loggerFactory);

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

        if (!CanApplyTo(eventData))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(
                    "Query hint '{Hint}' skipped: provider '{Provider}' is not in the allowed set.",
                    hint, eventData.Context?.Database.ProviderName ?? "<unknown>");
            return;
        }

        var trimmed = command.CommandText.TrimEnd();
        command.CommandText = trimmed.EndsWith(";")
            ? $"{trimmed[..^1]} {hint};"
            : $"{trimmed} {hint}";
    }

    /// <summary>Provider guard: true when the hint may be applied to this command.</summary>
    protected virtual bool CanApplyTo(CommandEventData eventData)
    {
        if (_canApply is not null)
        {
            try { return _canApply(eventData); }
            catch { return false; }
        }
        if (_allowedProviders is null || _allowedProviders.Count == 0) return true;
        var provider = eventData.Context?.Database.ProviderName;
        if (string.IsNullOrEmpty(provider)) return false;
        return _allowedProviders.Any(p => provider.Contains(p, StringComparison.OrdinalIgnoreCase));
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
