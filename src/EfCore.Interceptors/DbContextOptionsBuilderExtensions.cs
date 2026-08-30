using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors;

/// <summary>Registration entry points for the interceptor suite.</summary>
public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Registers a configured set of EF Core interceptors on this context:
    /// <code>
    /// optionsBuilder
    ///     .UseSqlServer(connString)
    ///     .UseEfInterceptors(s => s
    ///         .WithAuditing(users)
    ///         .WithSoftDeletes(users)
    ///         .WithSlowQueryWarning(TimeSpan.FromSeconds(2)));
    /// </code>
    /// </summary>
    public static DbContextOptionsBuilder UseEfInterceptors(
        this DbContextOptionsBuilder optionsBuilder,
        Action<EfInterceptorsSetup>? configure = null)
    {
        var setup = new EfInterceptorsSetup();
        configure?.Invoke(setup);
        setup.BuildInto(optionsBuilder);
        return optionsBuilder;
    }

    /// <summary>Generic overload for derived options builders.</summary>
    public static TBuilder UseEfInterceptors<TBuilder>(
        this TBuilder optionsBuilder,
        Action<EfInterceptorsSetup>? configure = null)
        where TBuilder : DbContextOptionsBuilder
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseEfInterceptors(configure);
        return optionsBuilder;
    }
}
