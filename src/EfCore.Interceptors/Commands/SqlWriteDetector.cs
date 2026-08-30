using System.Text.RegularExpressions;

namespace EfCore.Interceptors.Commands;

/// <summary>Heuristic detection of write statements in SQL text.</summary>
internal static partial class SqlWriteDetector
{
    [GeneratedRegex(
        @"\b(insert\s+into|insert|update\s+\w+\s+set|update|delete\s+from|delete|merge|truncate|create\s+(table|index|view)|alter\s+table|drop\s+(table|index|view))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteRegex();

    public static bool IsWrite(string sql) => WriteRegex().IsMatch(sql);
}
