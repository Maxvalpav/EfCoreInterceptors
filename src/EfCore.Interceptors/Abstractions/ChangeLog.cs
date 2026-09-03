namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Excludes the property from <c>ChangeLog</c> diffs (security S-5).
/// Use for secrets, PII or otherwise sensitive values that must never land in the audit trail,
/// even in encrypted form. <c>[Encrypted]</c> properties are redacted automatically and do not
/// need this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExcludeFromChangeLogAttribute : Attribute;
