namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Restricts a property to roles (03.3): readers without a role see the default value,
/// writers without a role get <see cref="FieldAccessException"/>. Compose with
/// <c>[Encrypted]</c> (authorize after decryption — register field authorization last).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RequiresRoleAttribute(params string[] roles) : Attribute
{
    public string[] Roles { get; } = roles;
}

/// <summary>Ambient roles of the current principal (request claims, test stub, …).</summary>
public interface IRoleProvider
{
    bool IsInRole(string role);
}

/// <summary>Fixed roles — handy for tests and single-role services.</summary>
public sealed class StaticRoleProvider(params string[] roles) : IRoleProvider
{
    private readonly HashSet<string> _roles = new(roles, StringComparer.OrdinalIgnoreCase);
    public bool IsInRole(string role) => _roles.Contains(role);
}

/// <summary>Raised when code without the required role modifies a protected property.</summary>
public sealed class FieldAuthorizationException(string message) : UnauthorizedAccessException(message);
