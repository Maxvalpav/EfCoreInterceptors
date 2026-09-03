using System.Linq.Expressions;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Queries;

/// <summary>
/// Compliance guard evaluated at query-compilation time: rejects forbidden query shapes such as
/// IgnoreQueryFilters() (bypasses soft-delete/tenant filters), ExecuteDelete() and ExecuteUpdate()
/// by throwing <see cref="QueryPolicyViolationException"/>.
/// With <c>forbidUntaggedRawSql</c>, raw-SQL queries (<c>FromSqlRaw</c>/<c>SqlQuery</c> — which
/// bypass query filters entirely) are rejected unless the query is tagged
/// <c>TagWith("tenant-safe")</c> (security S-2: explicit allow-list for filter-bypassing SQL).
/// Note: EF caches compiled queries per shape, so the check runs once per unique query.
/// </summary>
public class StrictQueryPolicyQueryExpressionInterceptor(
    bool forbidIgnoreQueryFilters = true,
    bool forbidExecuteDelete = false,
    bool forbidExecuteUpdate = false,
    bool forbidUntaggedRawSql = false,
    string rawSqlBypassTag = "tenant-safe") : IQueryExpressionInterceptor
{
    private readonly bool _forbidIgnoreQueryFilters = forbidIgnoreQueryFilters;
    private readonly bool _forbidExecuteDelete = forbidExecuteDelete;
    private readonly bool _forbidExecuteUpdate = forbidExecuteUpdate;
    private readonly bool _forbidUntaggedRawSql = forbidUntaggedRawSql;
    private readonly string _rawSqlBypassTag = rawSqlBypassTag;

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        var visitor = new PolicyVisitor(this);
        visitor.Visit(queryExpression);
        if (_forbidUntaggedRawSql && visitor.FoundRawSql && !visitor.HasBypassTag(_rawSqlBypassTag))
        {
            throw new QueryPolicyViolationException(
                $"Raw SQL (FromSql/SqlQuery) bypasses tenant/soft-delete filters and is forbidden by query policy. " +
                $"Mark reviewed queries with TagWith(\"{_rawSqlBypassTag}\").");
        }
        return queryExpression;
    }

    private sealed class PolicyVisitor(StrictQueryPolicyQueryExpressionInterceptor owner) : ExpressionVisitor
    {
        public bool FoundRawSql;
        private readonly HashSet<string> _tags = new(StringComparer.Ordinal);

        public bool HasBypassTag(string bypassTag) => _tags.Any(t =>
            t.Equals(bypassTag, StringComparison.Ordinal)
            || t.StartsWith(bypassTag + ":", StringComparison.Ordinal)
            || t.StartsWith(bypassTag + " ", StringComparison.Ordinal));

        public override Expression? Visit(Expression? node)
        {
            if (node is MethodCallExpression call)
            {
                owner.Check(call.Method.Name);
                if (call.Method.Name == "TagWith" && call.Arguments.Count == 2
                    && call.Arguments[1] is ConstantExpression { Value: string tag })
                {
                    _tags.Add(tag);
                }
            }

            return base.Visit(node);
        }

        /// <summary>
        /// Raw-SQL query roots (<c>FromSqlRaw</c>/<c>SqlQuery</c>) surface as internal
        /// extension expressions (<c>FromSqlQueryRootExpression</c>/
        /// <c>SqlQueryRootExpression</c>), not method calls — match by type name.
        /// </summary>
        protected override Expression VisitExtension(Expression node)
        {
            var typeName = node.GetType().Name;
            if (typeName.Contains("FromSql", StringComparison.Ordinal)
                || typeName.Contains("SqlQuery", StringComparison.Ordinal))
            {
                FoundRawSql = true;
            }
            return base.VisitExtension(node);
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
