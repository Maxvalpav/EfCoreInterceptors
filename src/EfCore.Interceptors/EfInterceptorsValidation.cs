using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors;

/// <summary>Severity of a startup-validation finding (I-1).</summary>
public enum InterceptorValidationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>One startup-validation finding: stable <c>Code</c> + human-readable <c>Message</c>.</summary>
public sealed record InterceptorValidationIssue(
    InterceptorValidationSeverity Severity,
    string Code,
    string Message);

/// <summary>
/// Startup configuration validator (I-1): catches "silent" misconfigurations before the first
/// query — soft delete without a query filter, ChangeLog/Outbox without mapped entities,
/// duplicated interceptors, command interceptors on non-relational providers.
/// Analog of <c>IValidateOptions&lt;T&gt;</c>: call at startup and fail fast or log.
/// </summary>
public static class EfInterceptorsValidator
{
    /// <summary>
    /// Validates <paramref name="interceptors"/> against the model of <paramref name="context"/>.
    /// Pure (no queries, no saves) — safe to call at startup or in tests.
    /// </summary>
    public static IReadOnlyList<InterceptorValidationIssue> Validate(
        IEnumerable<IInterceptor> interceptors,
        DbContext context)
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        ArgumentNullException.ThrowIfNull(context);
        var list = interceptors.ToArray();
        var issues = new List<InterceptorValidationIssue>();

        var hasSoftDelete = list.OfType<Saving.SoftDeleteSaveChangesInterceptor>().Any();
        var hasChangeLog = list.OfType<Saving.ChangeLogSaveChangesInterceptor>().Any();
        var hasOutbox = list.OfType<Saving.OutboxSaveChangesInterceptor>().Any();
        var hasMultiTenancy = list.OfType<Saving.MultiTenancySaveChangesInterceptor>().Any();
        var hasStrictPolicy = list.OfType<Queries.StrictQueryPolicyQueryExpressionInterceptor>().Any();
        var hasCommands = list.Any(i => i is DbCommandInterceptor);

        if (hasSoftDelete)
        {
            foreach (var entityType in context.Model.GetEntityTypes())
            {
                if (entityType.IsOwned()) continue;
                if (!typeof(ISoftDeletableEntity).IsAssignableFrom(entityType.ClrType)) continue;
                if (entityType.GetQueryFilter() is null)
                {
                    issues.Add(new InterceptorValidationIssue(InterceptorValidationSeverity.Error, "EFI2002",
                        $"Soft delete is enabled but '{entityType.ClrType.Name}' has no query filter — " +
                        "deleted rows will stay visible. Call modelBuilder.ApplySoftDeleteFilters() " +
                        "or add HasQueryFilter(e => !e.IsDeleted)."));
                }
            }
        }

        if (hasChangeLog && context.Model.FindEntityType(typeof(ChangeLogEntry)) is null)
        {
            issues.Add(new InterceptorValidationIssue(InterceptorValidationSeverity.Error, "EFI2010",
                "WithChangeLog is enabled but ChangeLogEntry is not mapped — " +
                "call modelBuilder.Entity<ChangeLogEntry>() in OnModelCreating."));
        }

        if (hasOutbox && context.Model.FindEntityType(typeof(OutboxMessage)) is null)
        {
            issues.Add(new InterceptorValidationIssue(InterceptorValidationSeverity.Error, "EFI2011",
                "WithOutbox is enabled but OutboxMessage is not mapped — " +
                "call modelBuilder.Entity<OutboxMessage>() in OnModelCreating."));
        }

        foreach (var group in list.GroupBy(i => i.GetType()).Where(g => g.Count() > 1))
        {
            issues.Add(new InterceptorValidationIssue(InterceptorValidationSeverity.Warning, "EFI2012",
                $"Interceptor '{group.Key.Name}' is registered {group.Count()} times — " +
                "each instance runs on every SaveChanges. Register once (or use AddOrReplace semantics)."));
        }

        if (hasCommands && !context.Database.IsRelational())
        {
            issues.Add(new InterceptorValidationIssue(InterceptorValidationSeverity.Warning, "EFI2013",
                $"Command interceptors are registered with non-relational provider " +
                $"'{context.Database.ProviderName}' — they will never be invoked."));
        }

        if (hasMultiTenancy && !hasStrictPolicy)
        {
            issues.Add(new InterceptorValidationIssue(InterceptorValidationSeverity.Info, "EFI2014",
                "Multi-tenancy without a strict query policy: tenant filters can be bypassed via " +
                "IgnoreQueryFilters()/raw SQL. Expected only for admin contexts — otherwise keep " +
                "the default WithMultiTenancy strict policy or add WithStrictQueryPolicy."));
        }

        return issues;
    }

    /// <summary>Validates and throws on the first <c>Error</c>; warnings/info are returned.</summary>
    public static IReadOnlyList<InterceptorValidationIssue> ValidateAndThrow(
        IEnumerable<IInterceptor> interceptors,
        DbContext context)
    {
        var issues = Validate(interceptors, context);
        var firstError = issues.FirstOrDefault(i => i.Severity == InterceptorValidationSeverity.Error);
        if (firstError is not null)
        {
            throw new InvalidOperationException(
                $"EfCore.Interceptors misconfigured [{firstError.Code}]: {firstError.Message}");
        }
        return issues;
    }

    /// <summary>
    /// Lifetime check (audit §3.4): <see cref="ICurrentUserProvider"/> is usually request-scoped,
    /// while <see cref="ServiceCollectionExtensions.AddEfInterceptors"/> registers interceptors as
    /// singletons. A scoped provider captured by a singleton leaks the actor across requests
    /// (or touches a disposed scope). Warns (EFI2020) when a scoped provider meets singleton
    /// interceptors; fix by registering actor-aware interceptors as scoped instead of via the helper.
    /// </summary>
    public static IReadOnlyList<InterceptorValidationIssue> ValidateLifetimes(
        Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var issues = new List<InterceptorValidationIssue>();
        var provider = services.LastOrDefault(d =>
            d.ServiceType == typeof(ICurrentUserProvider) || d.ServiceType == typeof(ITenantProvider));
        var hasSingletonInterceptors = services.Any(d =>
            d.ServiceType == typeof(IInterceptor) &&
            d.Lifetime == Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton);
        if (provider is not null &&
            provider.Lifetime == Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped &&
            hasSingletonInterceptors)
        {
            issues.Add(new InterceptorValidationIssue(InterceptorValidationSeverity.Warning, "EFI2020",
                $"'{provider.ServiceType.Name}' is Scoped but interceptors are Singleton — " +
                "the singleton will capture the first scope's actor/tenant. Register actor-aware " +
                "interceptors as Scoped manually instead of AddEfInterceptors, or use a static actor for background jobs."));
        }
        return issues;
    }
}
