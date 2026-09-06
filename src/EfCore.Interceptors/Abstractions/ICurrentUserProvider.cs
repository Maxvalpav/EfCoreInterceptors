namespace EfCore.Interceptors.Abstractions;

/// <summary>Resolves the current principal's name for audit fields.</summary>
public interface ICurrentUserProvider
{
    /// <summary>Name of the current user, or <see langword="null"/> when anonymous.</summary>
    string? UserName { get; }
}

/// <summary>Returns a fixed user name; handy for background jobs and tests.</summary>
public sealed class StaticCurrentUserProvider(string userName) : ICurrentUserProvider
{
    public static readonly StaticCurrentUserProvider System = new("system");

    /// <summary>Explicit anonymous actor — prefer over null/empty so audit trails stay explicit.</summary>
    public static readonly StaticCurrentUserProvider Anonymous = new("anonymous");

    public string UserName => userName;
}

/// <summary>
/// Actor policy when no user is authenticated (audit §3.4): fail fast instead of
/// silently writing an empty string into audit fields.
/// </summary>
public enum MissingActorPolicy
{
    /// <summary>Write <c>"system"</c> (background jobs, seed, default).</summary>
    System,
    /// <summary>Write <c>"anonymous"</c>.</summary>
    Anonymous,
    /// <summary>Throw <see cref="MissingActorException"/> when the provider returns null/empty.</summary>
    Throw,
}

/// <summary>Raised when audit stamping requires an actor but none is available.</summary>
public sealed class MissingActorException(string message) : InvalidOperationException(message);
