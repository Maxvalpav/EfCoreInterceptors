namespace EfCore.Interceptors.Queries;

/// <summary>
/// PostgreSQL locking hints (FOR UPDATE/SHARE) for <see cref="Commands.QueryHintsCommandInterceptor"/>.
/// Use TagWith("for_update") etc.; the interceptor appends the corresponding suffix.
/// See https://www.postgresql.org/docs/current/sql-select.html#SQL-FOR-UPDATE-SHARE
/// </summary>
public static class PostgresQueryHints
{
    public const string ForUpdate = "FOR UPDATE";
    public const string ForNoKeyUpdate = "FOR NO KEY UPDATE";
    public const string ForShare = "FOR SHARE";
    public const string ForKeyShare = "FOR KEY SHARE";

    public const string ForUpdateNowait = "FOR UPDATE NOWAIT";
    public const string ForUpdateSkipLocked = "FOR UPDATE SKIP LOCKED";
    public const string ForShareNowait = "FOR SHARE NOWAIT";
    public const string ForShareSkipLocked = "FOR SHARE SKIP LOCKED";

    public static IReadOnlyDictionary<string, string> Defaults { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["for_update"] = ForUpdate,
            ["for_update_nowait"] = ForUpdateNowait,
            ["for_update_skip_locked"] = ForUpdateSkipLocked,
            ["for_share"] = ForShare,
            ["for_share_nowait"] = ForShareNowait,
            ["for_share_skip_locked"] = ForShareSkipLocked,
            ["for_no_key_update"] = ForNoKeyUpdate,
            ["for_key_share"] = ForKeyShare,
        };
}
