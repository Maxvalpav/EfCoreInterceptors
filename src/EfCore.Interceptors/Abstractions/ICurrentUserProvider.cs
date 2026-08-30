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

    public string UserName => userName;
}
