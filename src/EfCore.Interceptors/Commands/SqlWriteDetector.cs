using System.Text.RegularExpressions;

namespace EfCore.Interceptors.Commands;

/// <summary>Heuristic detection of write statements in SQL text.</summary>
internal static partial class SqlWriteDetector
{
    [GeneratedRegex(
        @"^\s*(?:with\b[\s\S]*?\)\s*)?(insert|update|delete|merge|truncate|drop|alter|create)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteRegex();

    public static bool IsWrite(string sql) => WriteRegex().IsMatch(sql);
}
