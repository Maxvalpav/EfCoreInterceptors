using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Queries;

/// <summary>
/// Logs the LINQ expression tree of every query right before it is compiled.
/// Also serves as an extension point: derive from this class and override
/// <see cref="Transform"/> to rewrite query expressions programmatically
/// (the returned expression is what EF compiles).
/// </summary>
public class QueryTreeLoggingInterceptor(
    ILoggerFactory? loggerFactory = null) : IQueryExpressionInterceptor
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.Query") ?? NullLogger.Instance;

    public Expression QueryCompilationStarting(
        Expression queryExpression,
        QueryExpressionEventData eventData)
    {
        var transformed = Transform(queryExpression);
        _logger.LogDebug("Query expression tree:{NewLine}{Tree}",
            Environment.NewLine,
            Print(transformed));
        return transformed;
    }

    /// <summary>Hook for derived classes to rewrite the expression tree. Default: pass-through.</summary>
    protected virtual Expression Transform(Expression queryExpression) => queryExpression;

    private static string Print(Expression expression)
    {
        var sb = new StringBuilder();
        Append(expression, 0);
        return sb.ToString();

        void Append(Expression node, int depth)
        {
            sb.Append(' ', depth * 2).AppendLine(node.ToString());
        }
    }
}
