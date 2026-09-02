using System.Text.RegularExpressions;

namespace EfCore.Interceptors.Commands;

/// <summary>Heuristic detection of write statements in SQL text.</summary>
internal static partial class SqlWriteDetector
{
    [GeneratedRegex(
        @"^\s*(?:with\b[\s\S]*?\)\s*)?(insert|update|delete|merge|truncate|drop|alter|create|replace|grant|revoke|copy|exec|call)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteRegex();

    public static bool IsWrite(string sql) => WriteRegex().IsMatch(StripLeadingComments(sql));

    private static string StripLeadingComments(string sql)
    {
        var s = sql.TrimStart();
        while (true)
        {
            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                var nl = s.IndexOf('\n');
                if (nl < 0) return string.Empty;
                s = s[(nl + 1)..].TrimStart();
                continue;
            }
            if (s.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = s.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) return string.Empty;
                s = s[(end + 2)..].TrimStart();
                continue;
            }
            break;
        }
        return s;
    }
}
