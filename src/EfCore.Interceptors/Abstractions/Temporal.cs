namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Opts an entity into system-versioned history (03.1, SCD Type 2): every insert, update
/// and delete appends a version row; point-in-time reads go through
/// <c>TemporalQuery.AsOfAsync</c>. Requires <c>modelBuilder.Entity&lt;TemporalRecord&gt;()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TemporalAttribute : Attribute;
