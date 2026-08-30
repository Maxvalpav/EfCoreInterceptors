using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EfCore.Interceptors;

/// <summary>DI integration for the interceptor suite.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Builds the interceptor set at configuration time and registers each instance in the container
    /// so they can be resolved later from an <see cref="IServiceProvider"/>:
    /// <code>
    /// services.AddEfInterceptors(s =&gt; s.WithAuditing().WithSoftDeletes());
    /// services.AddDbContext&lt;AppDbContext&gt;((sp, options) =&gt;
    ///     options.UseSqlServer(cs).UseEfInterceptorsFrom(sp));
    /// </code>
    /// For scoped dependencies (e.g. a per-request current user), register your own
    /// interceptor instances instead — see README.
    /// </summary>
    public static IServiceCollection AddEfInterceptors(
        this IServiceCollection services,
        Action<EfInterceptorsSetup>? configure = null)
    {
        var setup = new EfInterceptorsSetup();
        configure?.Invoke(setup);

        foreach (var interceptor in setup.Interceptors)
        {
            // Stateless interceptors are safe to share singleton. Scoped interceptors (e.g. per-request user)
            // should be registered manually, not via this helper — see README.
            services.AddSingleton<IInterceptor>(interceptor);
            // Also register concrete type for direct resolution
            services.AddSingleton(interceptor.GetType(), interceptor);
        }

        return services;
    }

    /// <summary>
    /// Adds every <see cref="IInterceptor"/> previously registered in the container
    /// (e.g. via <see cref="AddEfInterceptors"/>) to this context's options.
    /// </summary>
    public static DbContextOptionsBuilder UseEfInterceptorsFrom(
        this DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider)
    {
        var interceptors = serviceProvider.GetServices<IInterceptor>().ToArray();
        if (interceptors.Length > 0)
        {
            optionsBuilder.AddInterceptors(interceptors);
        }

        return optionsBuilder;
    }
}
