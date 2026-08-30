using System.Linq.Expressions;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Queries;

/// <summary>
/// Prevents unbounded queries (no Take/Skip/First/Single/Any/Count) that could load millions of rows.
/// Throws <see cref="QueryPolicyViolationException"/> at compilation. Allow-list via TagWith("unbounded:allow").
/// Uses ExpressionVisitor instead of ToString() for performance and correctness.
/// </summary>
public class UnboundedQueryGuardInterceptor(int maxRows = 0) : IQueryExpressionInterceptor
{
    private readonly int _maxRows = maxRows;

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        if (_maxRows < 0) return queryExpression;

        var visitor = new GuardVisitor(_maxRows);
        visitor.Visit(queryExpression);

        if (visitor.HasUnboundedAllowTag) return queryExpression;

        if (visitor.HasEntityQueryable)
        {
            if (!visitor.HasLimitingOperator)
            {
                throw new QueryPolicyViolationException(
                    _maxRows == 0
                        ? "Unbounded query detected: add .Take(n)/.First() or TagWith(\"unbounded:allow\")."
                        : $"Unbounded query detected: add .Take({_maxRows}) or less, or TagWith(\"unbounded:allow\").");
            }

            if (_maxRows > 0 && visitor.TakeCount.HasValue && visitor.TakeCount.Value > _maxRows)
            {
                throw new QueryPolicyViolationException(
                    $"Query Take({visitor.TakeCount.Value}) exceeds maxRows {_maxRows}. Reduce Take or increase maxRows, or TagWith(\"unbounded:allow\").");
            }
        }

        return queryExpression;
    }

    private sealed class GuardVisitor(int maxRows) : ExpressionVisitor
    {
        public bool HasLimitingOperator { get; private set; }
        public bool HasEntityQueryable { get; private set; }
        public bool HasUnboundedAllowTag { get; private set; }
        public int? TakeCount { get; private set; }

        private readonly int _maxRows = maxRows;

        private static readonly HashSet<string> LimitingMethods = new(StringComparer.Ordinal)
        {
            "Take", "TakeWhile", "First", "FirstOrDefault", "Single", "SingleOrDefault",
            "Any", "All", "Count", "LongCount", "Contains", "ElementAt", "ElementAtOrDefault",
            "Last", "LastOrDefault", "Max", "Min", "Sum", "Average", "Find"
        };

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (LimitingMethods.Contains(node.Method.Name))
            {
                HasLimitingOperator = true;
            }

            if (node.Method.Name == "Take" && node.Arguments.Count > 1)
            {
                if (TryGetConstantInt(node.Arguments[1], out var n))
                {
                    TakeCount = n;
                }
            }

            // Detect TagWith("unbounded:allow") - tag is at Arguments[1] (source is [0])
            if (node.Method.Name == "TagWith" && node.Arguments.Count > 1)
            {
                foreach (var arg in node.Arguments.Skip(1))
                {
                    if (arg.Type == typeof(string) && TryGetConstantString(arg, out var tag) && tag.Contains("unbounded:allow", StringComparison.Ordinal))
                    {
                        HasUnboundedAllowTag = true;
                        break;
                    }
                }
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            // EntityQueryable appears as constant of type starting with "EntityQueryable"
            if (node.Value?.GetType().Name.Contains("EntityQueryable", StringComparison.Ordinal) == true)
            {
                HasEntityQueryable = true;
            }

            return base.VisitConstant(node);
        }

        protected override Expression VisitExtension(Expression node)
        {
            // EF Core query expression nodes (QueryRootExpression etc.) override ToString but are Extension nodes
            // Check type name for EntityQueryable
            if (node.GetType().Name.Contains("EntityQueryable", StringComparison.Ordinal)
                || node.GetType().Name.Contains("QueryRoot", StringComparison.Ordinal))
            {
                HasEntityQueryable = true;
            }

            return base.VisitExtension(node);
        }

        private static bool TryGetConstantString(Expression expr, out string value)
        {
            if (expr is ConstantExpression ce && ce.Value is string s)
            {
                value = s;
                return true;
            }

            // Try to evaluate member access of constant (e.g. closure)
            try
            {
                if (expr is MemberExpression me && me.Expression is ConstantExpression)
                {
                    var lambda = Expression.Lambda<Func<string>>(Expression.Convert(expr, typeof(string)));
                    value = lambda.Compile(preferInterpretation: true)();
                    return true;
                }
            }
            catch { }

            value = string.Empty;
            return false;
        }

        private static bool TryGetConstantInt(Expression expr, out int value)
        {
            if (expr is ConstantExpression ce && ce.Value is int i)
            {
                value = i;
                return true;
            }

            try
            {
                var lambda = Expression.Lambda<Func<int>>(Expression.Convert(expr, typeof(int)));
                value = lambda.Compile(preferInterpretation: true)();
                return true;
            }
            catch { }

            value = 0;
            return false;
        }
    }
}
