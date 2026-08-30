using System.Text.RegularExpressions;

namespace EfCore.Interceptors.Queries;

/// <summary>
/// Ready-made SQL Server query hints for <c>WithQueryHints</c>/<c>WithQueryHintsByTags</c>-style
/// setups. Apply only on SQL Server targets — other providers will reject these suffixes.
/// </summary>
public static class SqlServerQueryHints
{
    public const string Recompile = "OPTION (RECOMPILE)";

    public const string ForceOrder = "OPTION (FORCE ORDER)";

    public const string OptimizeForUnknown = "OPTION (OPTIMIZE FOR UNKNOWN)";

    public static string MaxDop(int degreeOfParallelism)
        => $"OPTION (MAXDOP {degreeOfParallelism})";

    public static string FastFirstRow(int rows)
        => $"OPTION (FAST {rows})";

    /// <summary>Sensible default mapping: TagWith("recompile") → OPTION (RECOMPILE).</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["recompile"] = Recompile
        };
}
