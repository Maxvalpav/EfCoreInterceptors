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

                    try
                    {
                        var value = Expression.Lambda<Func<string>>(argument).Compile()();
                        if (!string.IsNullOrEmpty(value))
                        {
                            Tags.Add(value);
                        }
                    }
                    catch
                    {
                        // Non-constant tag expressions are ignored for enforcement purposes.
                    }
                }
            }

            return base.VisitMethodCall(node);
        }
    }
}
