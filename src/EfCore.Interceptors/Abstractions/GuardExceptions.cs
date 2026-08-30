namespace EfCore.Interceptors.Abstractions;

/// <summary>Raised when a delete of an IProtectedEntity is attempted.</summary>
public sealed class ProtectedEntityException(string message) : InvalidOperationException(message);

/// <summary>Raised when an IImmutableEntity is modified or deleted.</summary>
public sealed class ImmutableEntityException(string message) : InvalidOperationException(message);
