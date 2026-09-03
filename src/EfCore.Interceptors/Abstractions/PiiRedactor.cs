using System.Text.RegularExpressions;

namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Ready-made PII redactors for SQL logging (02.9, 07.8): pass
/// <c>PiiRedactor.Default</c> as <c>textRedactor</c> to <c>WithSqlLogging</c>
/// instead of hand-rolling masks. All patterns run with timeouts (ReDoS-safe).
/// Card masking additionally validates the Luhn checksum to avoid mangling
/// innocent digit sequences (order ids, timestamps).
/// </summary>
public static partial class PiiRedactor
{
    /// <summary>Emails + phones + Luhn-valid card numbers + JWT-shaped tokens.</summary>
    public static Func<string, string> Default => static sql =>
    {
        // Park card candidates behind placeholders first: the phone pattern would
        // otherwise eat any long digit run before Luhn gets a vote (07.8).
        var slots = new List<Match>();
        var parked = CardDigits().Replace(sql, m =>
        {
            slots.Add(m);
            return $"\u0000{slots.Count - 1}\u0000";
        });
        var redacted = Jwt().Replace(ApplyPhoneAndEmail(parked), "***JWT***");
        for (var i = 0; i < slots.Count; i++)
            redacted = redacted.Replace($"\u0000{i}\u0000", MaskCard(slots[i]));
        return redacted;
    };

    /// <summary>Emails + phones (no card scan).</summary>
    public static Func<string, string> EmailAndPhone => static sql =>
        Phone().Replace(Email().Replace(sql, "***@***"), "***PHONE***");

    /// <summary>Validates a digit sequence with the Luhn checksum (card numbers).</summary>
    public static bool IsLuhnValid(string digits)
    {
        var sum = 0;
        var alternate = false;
        var count = 0;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var c = digits[i];
            if (c is < '0' or > '9')
            {
                if (c is ' ' or '-' or '.') continue;
                return false;
            }
            count++;
            var d = c - '0';
            if (alternate)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            alternate = !alternate;
        }
        return count is >= 13 and <= 19 && sum % 10 == 0;
    }

    private static string MaskCard(Match m)
    {
        var digits = m.Value;
        if (!IsLuhnValid(digits)) return digits; // not a card — leave alone
        var bare = digits.Replace(" ", "").Replace("-", "").Replace(".", "");
        return "****-****-****-" + bare[^4..];
    }

    private static string ApplyPhoneAndEmail(string sql) =>
        Phone().Replace(Email().Replace(sql, "***@***"), "***PHONE***");

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.None, 100)]
    private static partial Regex Email();

    [GeneratedRegex(@"\+?\d[\d\s.\-()]{7,}\d", RegexOptions.None, 100)]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b\d(?:[ \-\.]?\d){12,18}\b", RegexOptions.None, 100)]
    private static partial Regex CardDigits();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.None, 100)]
    private static partial Regex Jwt();
}
