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
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var tree = Print(transformed, maxLength: 2048);
            _logger.LogDebug("Query expression tree:{NewLine}{Tree}",
                Environment.NewLine,
                tree);
        }
        return transformed;
    }

    /// <summary>Hook for derived classes to rewrite the expression tree. Default: pass-through.</summary>
    protected virtual Expression Transform(Expression queryExpression) => queryExpression;

    private static string Print(Expression expression, int maxLength = 2048)
    {
        var visitor = new TreePrinter();
        visitor.Visit(expression);
        var s = visitor.Builder.ToString();
        if (s.Length > maxLength) s = s[..maxLength] + "... (truncated)";
        return s;
    }

    private sealed class TreePrinter : ExpressionVisitor
    {
        public StringBuilder Builder { get; } = new();
        private int _depth;
        protected override Expression VisitExtension(Expression node)
        {
            Builder.Append(' ', _depth * 2).AppendLine(node.ToString());
            _depth++;
            var r = base.VisitExtension(node);
            _depth--;
            return r;
        }
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Builder.Append(' ', _depth * 2).AppendLine(node.ToString());
            _depth++;
            var r = base.VisitMethodCall(node);
            _depth--;
            return r;
        }
        protected override Expression VisitParameter(ParameterExpression node)
        {
            Builder.Append(' ', _depth * 2).AppendLine(node.ToString());
            return base.VisitParameter(node);
        }
        protected override Expression VisitConstant(ConstantExpression node)
        {
            Builder.Append(' ', _depth * 2).AppendLine(node.ToString());
            return base.VisitConstant(node);
        }
    }
}
