using System.Linq.Expressions;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Queries;

/// <summary>
/// Compliance guard requiring queries to be tagged via <c>TagWith(...)</c>.
/// When a required-tags list is provided, every listed tag must be present on each query;
/// "require at least one" mode rejects completely untagged queries.
/// Makes "which feature issued this query" answerable from SQL comments in production traces.
/// Violations raise <see cref="QueryPolicyViolationException"/> at query-compilation time.
/// </summary>
public class RequireQueryTagsInterceptor : IQueryExpressionInterceptor
{
    private readonly string[] _requiredTags;
    private readonly bool _requireAtLeastOneTag;

    public RequireQueryTagsInterceptor(string[]? requiredTags = null, bool requireAtLeastOneTag = false)
    {
        _requiredTags = requiredTags ?? [];
        _requireAtLeastOneTag = requireAtLeastOneTag || _requiredTags.Length > 0;
    }

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        var tags = new TagCollector().Collect(queryExpression);

        if (_requireAtLeastOneTag && tags.Count == 0)
        {
            throw new QueryPolicyViolationException(
                "Query has no TagWith(...) — every query must carry at least one tag.");
        }

        var missing = _requiredTags.Where(t => !tags.Contains(t)).ToArray();
        if (missing.Length > 0)
        {
            throw new QueryPolicyViolationException(
                $"Query is missing required tag(s): {string.Join(", ", missing)}. " +
                $"Present tags: [{string.Join(", ", tags)}].");
        }

        return queryExpression;
    }

    private sealed class TagCollector : ExpressionVisitor
    {
        public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Collect(Expression expression)
        {
            Visit(expression);
            return Tags;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == nameof(EntityFrameworkQueryableExtensions.TagWith))
            {
                // TagWith(source, tag[, args]): evaluate every string argument except the source.
                foreach (var argument in node.Arguments.Skip(1))
                {
                    if (argument.Type != typeof(string))
                    {
                        continue;
                    }

                    if (TryExtractString(argument, out var value) && !string.IsNullOrEmpty(value))
                    {
                        Tags.Add(value);
                    }
                }
            }

            return base.VisitMethodCall(node);
        }

        private static bool TryExtractString(Expression expr, out string value)
        {
            if (expr is ConstantExpression ce && ce.Value is string s)
            {
                value = s;
                return true;
            }

            // For member access of closure constant, evaluate without Compile() overhead
            // by extracting constant value directly if possible
            if (expr is MemberExpression me && me.Expression is ConstantExpression closure)
            {
                try
                {
                    var field = me.Member as System.Reflection.FieldInfo;
                    if (field != null)
                    {
                        value = field.GetValue(closure.Value) as string ?? string.Empty;
                        return !string.IsNullOrEmpty(value);
                    }

                    var prop = me.Member as System.Reflection.PropertyInfo;
                    if (prop != null)
                    {
                        value = prop.GetValue(closure.Value) as string ?? string.Empty;
                        return !string.IsNullOrEmpty(value);
                    }
                }
                catch { }
            }

            try
            {
                // Fallback: interpret without JIT
                var lambda = Expression.Lambda<Func<string>>(Expression.Convert(expr, typeof(string)));
                value = lambda.Compile(preferInterpretation: true)();
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                value = string.Empty;
                return false;
            }
        }
    }
}
