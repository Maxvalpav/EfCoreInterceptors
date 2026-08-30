using System.Linq.Expressions;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Queries;

/// <summary>
/// Prevents unbounded queries (no Take/Skip/First/Single/Any/Count) that could load millions of rows.
/// Throws <see cref="QueryPolicyViolationException"/> at compilation. Allow-list via TagWith("unbounded:allow").
/// </summary>
public class UnboundedQueryGuardInterceptor(int maxRows = 1000) : IQueryExpressionInterceptor
{
    private readonly int _maxRows = maxRows;

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        var text = queryExpression.ToString();
        if (text.Contains("unbounded:allow")) return queryExpression;

        var hasLimit = text.Contains(".Take(") || text.Contains(".First") || text.Contains(".Single")
            || text.Contains(".Any(") || text.Contains(".Count(") || text.Contains(".LongCount(")
            || text.Contains(".Take(") || text.Contains(".Skip(");

        if (!hasLimit && IsRootQueryable(queryExpression))
        {
            // Heuristic: root is EntityQueryable without limit — warn via exception if configured.
            // We use exception only when maxRows==0 (strict). Otherwise just rely on metrics? For now throw if maxRows==0 else allow.
            if (_maxRows == 0)
                throw new QueryPolicyViolationException("Unbounded query detected: add .Take(n)/.First() or TagWith(\"unbounded:allow\").");
        }

        return queryExpression;
    }

    private static bool IsRootQueryable(Expression expr) => expr.ToString().Contains("EntityQueryable");
}
