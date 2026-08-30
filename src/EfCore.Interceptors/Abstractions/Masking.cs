namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Masks property value on materialization for DLP (e.g. email -> a***@example.com).
/// Use [Masked] on string properties; provide a real IMaskingPolicy in production.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MaskedAttribute : Attribute
{
    public MaskType MaskType { get; }
    public MaskedAttribute(MaskType maskType = MaskType.Email) => MaskType = maskType;
}

public enum MaskType { Email, Phone, Card, Full }

public interface IMaskingPolicy
{
    string Mask(string value, MaskType type);
}

public sealed class DefaultMaskingPolicy : IMaskingPolicy
{
    public string Mask(string value, MaskType type) => type switch
    {
        MaskType.Email => MaskEmail(value),
        MaskType.Phone => value.Length <= 4 ? "****" : new string('*', value.Length - 4) + value[^4..],
        MaskType.Card => value.Length <= 4 ? "****" : "****-****-****-" + value[^4..],
        _ => new string('*', value.Length)
    };

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***" + email[at..];
        return email[0] + new string('*', at - 1) + email[at..];
    }
}
