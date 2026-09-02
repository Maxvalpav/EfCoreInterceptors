using System.Linq.Expressions;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EfCore.Interceptors;

/// <summary>
/// One-line global query filters that complete the write-side interceptors:
/// <see cref="ApplySoftDeleteFilters"/> hides ISoftDeletableEntity rows marked deleted,
/// <see cref="ApplyTenantFilters"/> restricts ITenantEntity rows to the current tenant.
/// Both compose with any existing filters instead of overwriting them: an existing anonymous
/// filter is merged via AndAlso, and named filters stay untouched while these are added
/// under their own keys.
/// </summary>
public static class ModelBuilderFilterExtensions
{
    private const string SoftDeleteKey = "EfCoreInterceptors.SoftDelete";
    private const string TenantKey = "EfCoreInterceptors.Tenant";

    /// <summary>
    /// Adds <c>e =&gt; !e.IsDeleted</c> to every entity type implementing
    /// <see cref="ISoftDeletableEntity"/>.
    /// </summary>
    public static ModelBuilder ApplySoftDeleteFilters(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ISoftDeletableEntity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            Expression body = Expression.Not(
                Expression.Property(parameter, nameof(ISoftDeletableEntity.IsDeleted)));

            MergeFilter(entityType, SoftDeleteKey, parameter, body);
        }

        return modelBuilder;
    }

    /// <summary>
    /// Adds <c>e.TenantId == currentTenant</c> to every entity type implementing
    /// <see cref="ITenantEntity"/>; the tenant is resolved per query execution via the provider.
    /// <para><b>Important:</b> capturing an external <see cref="ITenantProvider"/> freezes the model
    /// in EF's <see cref="Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory"/> cache — first tenant sticks.
    /// Register <see cref="Model.TenantModelCacheKeyFactory"/> via
    /// <c>options.ReplaceService&lt;Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, Model.TenantModelCacheKeyFactory&gt;()</c>
    /// or make your <c>DbContext</c> expose <c>CurrentTenantId</c> and use filter
    /// <c>e => e.TenantId == ((AppDbContext)this).CurrentTenantId</c> inside OnModelCreating.</para>
    /// </summary>
    public static ModelBuilder ApplyTenantFilters(this ModelBuilder modelBuilder, ITenantProvider tenantProvider)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var tenantProperty = Expression.Property(parameter, nameof(ITenantEntity.TenantId));
            var currentTenant = Expression.Property(
                Expression.Constant(tenantProvider),
                nameof(ITenantProvider.CurrentTenantId));

            Expression body = Expression.Equal(tenantProperty, currentTenant);

            MergeFilter(entityType, TenantKey, parameter, body);
        }

        return modelBuilder;
    }

    /// <summary>
    /// Preferred overload without capturing an external provider: use a <c>CurrentTenantId</c> property on your DbContext
    /// (or implement <see cref="Model.ITenantProviderAccessor"/>). Call as:
    /// <c>modelBuilder.Entity&lt;Order&gt;().HasQueryFilter("EfCoreInterceptors.Tenant", e =&gt; e.TenantId == CurrentTenantId)</c>
    /// inside <c>OnModelCreating</c> where <c>CurrentTenantId</c> is a property of <c>this</c> DbContext.
    /// This avoids freezing the tenant in EF's <see cref="Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory"/> cache.
    /// If you must use <see cref="ApplyTenantFilters(ModelBuilder, ITenantProvider)"/>, register
    /// <see cref="Model.TenantModelCacheKeyFactory"/> via <c>options.ReplaceService&lt;IModelCacheKeyFactory, TenantModelCacheKeyFactory&gt;()</c>.
    /// </summary>
    public static ModelBuilder ApplyTenantFiltersWithContextProperty(this ModelBuilder modelBuilder, string tenantPropertyName = "CurrentTenantId")
    {
        // This helper is a documentation entry point; actual filter must be defined per-entity via HasQueryFilter
        // referencing the DbContext property (EF translates this as a parameter, not a constant).
        // We keep this method to surface the guidance in IntelliSense — callers should write their own HasQueryFilter.
        throw new NotSupportedException(
            $"Define tenant filter directly in OnModelCreating: modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == {tenantPropertyName}). " +
            "See TenantModelCacheKeyFactory.");
    }

    private static void MergeFilter(
        IMutableEntityType entityType,
        string featureKey,
        ParameterExpression parameter,
        Expression newBody)
    {
#if NET10_0_OR_GREATER
        var declared = entityType.GetDeclaredQueryFilters().ToList();
        switch (declared.Count)
        {
            case 0:
                entityType.SetQueryFilter(Expression.Lambda(newBody, parameter));
                return;
            case 1 when declared[0].IsAnonymous:
                var existing = declared[0].Expression!;
                var combined = Expression.AndAlso(Rebind(existing, parameter), newBody);
                entityType.SetQueryFilter(Expression.Lambda(combined, parameter));
                return;
            default:
                entityType.SetQueryFilter(featureKey, Expression.Lambda(newBody, parameter));
                return;
        }
#else
        // net8.0: no named filters — fold into single anonymous filter
        var existingFilter = entityType.GetQueryFilter();
        if (existingFilter is null)
        {
            entityType.SetQueryFilter(Expression.Lambda(newBody, parameter));
        }
        else
        {
            var combined = Expression.AndAlso(Rebind(existingFilter, parameter), newBody);
            entityType.SetQueryFilter(Expression.Lambda(combined, parameter));
        }
#endif
    }

    private static Expression Rebind(LambdaExpression filter, ParameterExpression target)
    {
        var sourceParameter = filter.Parameters.Single();
        return new ReplaceParameterVisitor(sourceParameter, target).Visit(filter.Body)!;
    }

    private sealed class ReplaceParameterVisitor(ParameterExpression from, ParameterExpression to)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
