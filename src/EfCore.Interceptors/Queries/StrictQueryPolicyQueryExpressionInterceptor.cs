using System.Linq.Expressions;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Queries;

/// <summary>
/// Compliance guard evaluated at query-compilation time: rejects forbidden query shapes such as
/// IgnoreQueryFilters() (bypasses soft-delete/tenant filters), ExecuteDelete() and ExecuteUpdate()
/// by throwing <see cref="QueryPolicyViolationException"/>.
/// Note: EF caches compiled queries per shape, so the check runs once per unique query.
/// </summary>
public class StrictQueryPolicyQueryExpressionInterceptor(
    bool forbidIgnoreQueryFilters = true,
    bool forbidExecuteDelete = false,
    bool forbidExecuteUpdate = false) : IQueryExpressionInterceptor
{
    private readonly bool _forbidIgnoreQueryFilters = forbidIgnoreQueryFilters;
    private readonly bool _forbidExecuteDelete = forbidExecuteDelete;
    private readonly bool _forbidExecuteUpdate = forbidExecuteUpdate;

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        new PolicyVisitor(this).Visit(queryExpression);
        return queryExpression;
    }

    private sealed class PolicyVisitor(StrictQueryPolicyQueryExpressionInterceptor owner) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is MethodCallExpression call)
            {
                owner.Check(call.Method.Name);
            }

            return base.Visit(node);
        }
    }

    private void Check(string methodName)
    {
        if (_forbidIgnoreQueryFilters && methodName == "IgnoreQueryFilters")
        {
            throw new QueryPolicyViolationException(
                "IgnoreQueryFilters() is forbidden by query policy (soft-delete/tenant filters must not be bypassed).");
        }

        if (_forbidExecuteDelete && methodName.StartsWith("ExecuteDelete", StringComparison.Ordinal))
        {
            throw new QueryPolicyViolationException("ExecuteDelete() is forbidden by query policy.");
        }

        if (_forbidExecuteUpdate && methodName.StartsWith("ExecuteUpdate", StringComparison.Ordinal))
        {
            throw new QueryPolicyViolationException("ExecuteUpdate() is forbidden by query policy.");
        }
    }
}
