using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace EfCore.Interceptors.Analyzers;

/// <summary>
/// EFI2001/EFI2003: compile-time guards for filter bypasses that the runtime policy
/// (<c>WithStrictQueryPolicy</c>) would reject — surfaced earlier, in the IDE.
/// Conservative like <see cref="BulkGuardAnalyzer"/>: syntax-first, best-effort.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StrictnessAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor EFI2001 = new("EFI2001",
        "IgnoreQueryFilters bypasses soft-delete/tenant filters",
        "IgnoreQueryFilters() bypasses soft-delete and tenant filters; " +
        "WithMultiTenancy enables a strict query policy by default — " +
        "keep this call only for admin/recycle-bin paths or suppress with justification",
        "EfCore.Interceptors", DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor EFI2003 = new("EFI2003",
        "Query over [Encrypted] property cannot match",
        "Where() over [Encrypted] '{0}' can never match: the column holds random-nonce " +
        "ciphertext, so equality/LIKE against plaintext always misses",
        "EfCore.Interceptors", DiagnosticSeverity.Warning, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(EFI2001, EFI2003);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx)
    {
        if (ctx.Node is not InvocationExpressionSyntax inv) return;
        var text = inv.Expression.ToString();
        if (text.EndsWith("IgnoreQueryFilters", System.StringComparison.Ordinal))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(EFI2001, inv.GetLocation()));
            return;
        }
        if (!text.EndsWith("Where", System.StringComparison.Ordinal)) return;

        // Where(predicate): inspect member accesses inside the lambda argument.
        var lambda = inv.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<LambdaExpressionSyntax>()
            .FirstOrDefault();
        if (lambda is null) return;
        var model = ctx.SemanticModel;
        foreach (var access in lambda.Body.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            // Member access `e.CardNumber` inside a lambda binds directly to the property symbol.
            if (model.GetSymbolInfo(access).Symbol is IPropertySymbol property && IsEncrypted(property))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(EFI2003, access.GetLocation(), property.Name));
            }
        }
    }

    private static bool IsEncrypted(IPropertySymbol property) =>
        property.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "EncryptedAttribute"
            || a.AttributeClass?.ToDisplayString() == "EfCore.Interceptors.Abstractions.EncryptedAttribute");
}
