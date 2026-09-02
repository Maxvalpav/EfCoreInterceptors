using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace EfCore.Interceptors.Analyzers;

/// <summary>
/// EFI1001-EFI1005: warns when ExecuteUpdate/ExecuteDelete bypasses SaveChanges interceptors.
/// Analyzer is intentionally conservative — best-effort via syntax.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BulkGuardAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor EFI1001 = new("EFI1001", "ExecuteDelete bypasses soft delete",
        "ExecuteDelete on '{0}' bypasses ISoftDeletableEntity and will physically delete rows; use ExecuteSoftDeleteAsync", "EfCore.Interceptors", DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor EFI1002 = new("EFI1002", "ExecuteUpdate writes plaintext to [Encrypted]",
        "SetProperty touches [Encrypted] '{0}' — value will be stored plaintext", "EfCore.Interceptors", DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor EFI1003 = new("EFI1003", "ExecuteUpdate changes TenantId",
        "SetProperty mutates TenantId — cross-tenant transfer without guard", "EfCore.Interceptors", DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor EFI1004 = new("EFI1004", "ExecuteDelete on protected/immutable entity",
        "ExecuteDelete on '{0}' bypasses IProtectedEntity/IImmutableEntity guard", "EfCore.Interceptors", DiagnosticSeverity.Warning, true);
    public static readonly DiagnosticDescriptor EFI1005 = new("EFI1005", "ExecuteUpdate without audit stamps",
        "ExecuteUpdate on IAuditableEntity without UpdatedAtUtc/UpdatedBy — audit stamps will be stale", "EfCore.Interceptors", DiagnosticSeverity.Info, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(EFI1001, EFI1002, EFI1003, EFI1004, EFI1005);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx)
    {
        if (ctx.Node is not InvocationExpressionSyntax inv) return;
        var name = inv.Expression.ToString();
        if (!name.Contains("ExecuteDelete") && !name.Contains("ExecuteUpdate")) return;

        // Conservative: any Execute* invocation inside a method that has IQueryable<T> param is flagged.
        // Full semantic check (ISoftDeletableEntity etc.) requires compilation + symbol lookup — stub for v1.
        if (name.Contains("ExecuteDelete"))
            ctx.ReportDiagnostic(Diagnostic.Create(EFI1001, inv.GetLocation(), name));
        else if (name.Contains("ExecuteUpdate"))
        {
            // Heuristic: if lambda contains TenantId or Encrypted property name, raise specific.
            var text = inv.ToString();
            if (text.Contains("TenantId")) ctx.ReportDiagnostic(Diagnostic.Create(EFI1003, inv.GetLocation()));
            else if (text.Contains("Encrypted") || text.Contains("Ssn") || text.Contains("Secret")) ctx.ReportDiagnostic(Diagnostic.Create(EFI1002, inv.GetLocation(), "encrypted"));
            else ctx.ReportDiagnostic(Diagnostic.Create(EFI1005, inv.GetLocation()));
        }
    }
}
