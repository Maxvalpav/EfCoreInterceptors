using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfCore.Interceptors.Generators;

/// <summary>
/// Emits <see cref="Abstractions"/> contract implementations for partial entity classes marked
/// with <c>[Auditable]</c>, <c>[SoftDeletable]</c> and/or <c>[HasDomainEvents]</c>:
/// audit columns, soft-delete columns and a <c>[NotMapped]</c> domain-event queue.
/// Non-partial targets produce EFI3001 and are skipped.
/// </summary>
[Generator]
public sealed class ContractGenerator : IIncrementalGenerator
{
    private const string AuditableMetadata = "EfCore.Interceptors.Abstractions.AuditableAttribute";
    private const string SoftDeletableMetadata = "EfCore.Interceptors.Abstractions.SoftDeletableAttribute";
    private const string HasDomainEventsMetadata = "EfCore.Interceptors.Abstractions.HasDomainEventsAttribute";

    private static readonly DiagnosticDescriptor NotPartialRule = new(
        "EFI3001",
        "Contract attribute requires a partial class",
        "EfCore.Interceptors contract attributes apply to partial classes only; '{0}' will not get generated members",
        "EfCore.Interceptors.Generators",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // One syntax pipeline for all three attributes (joined by class symbol downstream):
        // a single ForAttributeWithMetadataName per attribute cannot be joined by class, so
        // collect candidate classes once and resolve attributes from the compilation.
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, _) => (ClassDeclarationSyntax)ctx.Node)
            .Where(static c => c is not null);

        var compilationAndClasses = context.CompilationProvider.Combine(candidates.Collect());
        context.RegisterSourceOutput(compilationAndClasses, static (ctx, pair) =>
        {
            var compilation = pair.Left;
            var auditAttr = compilation.GetTypeByMetadataName(AuditableMetadata);
            var softAttr = compilation.GetTypeByMetadataName(SoftDeletableMetadata);
            var eventsAttr = compilation.GetTypeByMetadataName(HasDomainEventsMetadata);
            if (auditAttr is null && softAttr is null && eventsAttr is null) return;

            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var classSyntax in pair.Right.Distinct())
            {
                var symbol = compilation.GetSemanticModel(classSyntax.SyntaxTree)
                    .GetDeclaredSymbol(classSyntax) as INamedTypeSymbol;
                if (symbol is null || !seen.Add(symbol)) continue;

                var hasAudit = HasAttribute(symbol, auditAttr);
                var hasSoft = HasAttribute(symbol, softAttr);
                var hasEvents = HasAttribute(symbol, eventsAttr);
                if (!hasAudit && !hasSoft && !hasEvents) continue;

                if (!IsPartial(classSyntax))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(NotPartialRule, classSyntax.Identifier.GetLocation(), symbol.Name));
                    continue;
                }

                ctx.AddSource(HintName(symbol), Generate(symbol, hasAudit, hasSoft, hasEvents));
            }
        });
    }

    private static bool HasAttribute(INamedTypeSymbol symbol, INamedTypeSymbol? attribute)
    {
        if (attribute is null) return false;
        foreach (var attr in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attribute))
                return true;
        }
        return false;
    }

    private static bool IsPartial(ClassDeclarationSyntax syntax)
        => syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));

    private static string HintName(INamedTypeSymbol symbol)
    {
        var stack = new Stack<string>();
        var current = symbol;
        while (current is not null)
        {
            stack.Push(current.Name);
            current = current.ContainingType;
        }
        var fullName = string.Join("_", stack);
        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString() + ".";
        return (ns + fullName + ".Contracts.g.cs")
            .Replace('<', '_').Replace('>', '_');
    }

    private static string Generate(INamedTypeSymbol symbol, bool audit, bool soft, bool events)
    {
        var existing = new HashSet<string>(symbol.GetMembers().OfType<IPropertySymbol>().Select(p => p.Name));
        var existingMethods = new HashSet<string>(symbol.GetMembers().OfType<IMethodSymbol>().Select(m => m.Name));
        var existingFields = new HashSet<string>(symbol.GetMembers().OfType<IFieldSymbol>().Select(f => f.Name));

        var interfaces = new List<string>();
        if (audit) interfaces.Add("global::EfCore.Interceptors.Abstractions.IAuditableEntity");
        if (soft) interfaces.Add("global::EfCore.Interceptors.Abstractions.ISoftDeletableEntity");
        if (events) interfaces.Add("global::EfCore.Interceptors.Abstractions.IHasDomainEvents");

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// EfCore.Interceptors.Generators: contract members. Do not edit; the class must stay partial.");
        sb.AppendLine("#nullable enable");
        var ns = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();
        if (ns is not null)
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
        }

        var nesting = new Stack<INamedTypeSymbol>();
        for (var c = symbol.ContainingType; c is not null; c = c.ContainingType) nesting.Push(c);
        var indent = string.Empty;
        foreach (var outer in nesting)
        {
            sb.Append(indent).Append("partial class ").Append(outer.Name).AppendLine();
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        sb.Append(indent).Append("partial class ").Append(symbol.Name).Append(" : ").Append(string.Join(", ", interfaces)).AppendLine();
        sb.Append(indent).AppendLine("{");
        var memberIndent = indent + "    ";

        if (audit)
        {
            EmitProperty(sb, memberIndent, existing, "CreatedAtUtc", "global::System.DateTimeOffset");
            EmitProperty(sb, memberIndent, existing, "CreatedBy", "string?");
            EmitProperty(sb, memberIndent, existing, "UpdatedAtUtc", "global::System.DateTimeOffset?");
            EmitProperty(sb, memberIndent, existing, "UpdatedBy", "string?");
        }
        if (soft)
        {
            EmitProperty(sb, memberIndent, existing, "IsDeleted", "bool");
            EmitProperty(sb, memberIndent, existing, "DeletedAtUtc", "global::System.DateTimeOffset?");
            EmitProperty(sb, memberIndent, existing, "DeletedBy", "string?");
        }
        if (events)
        {
            if (!existingFields.Contains("_domainEvents"))
            {
                sb.Append(memberIndent).AppendLine("private readonly global::System.Collections.Generic.List<global::EfCore.Interceptors.Abstractions.IDomainEvent> _domainEvents = new();");
            }
            if (!existing.Contains("DomainEvents"))
            {
                sb.Append(memberIndent).AppendLine("[global::System.ComponentModel.DataAnnotations.Schema.NotMapped]");
                sb.Append(memberIndent).AppendLine("public global::System.Collections.Generic.IReadOnlyList<global::EfCore.Interceptors.Abstractions.IDomainEvent> DomainEvents => _domainEvents;");
            }
            if (!existingMethods.Contains("AddDomainEvent"))
            {
                sb.Append(memberIndent).AppendLine("public void AddDomainEvent(global::EfCore.Interceptors.Abstractions.IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);");
            }
            if (!existingMethods.Contains("ClearDomainEvents"))
            {
                sb.Append(memberIndent).AppendLine("public void ClearDomainEvents() => _domainEvents.Clear();");
            }
        }

        sb.Append(indent).AppendLine("}");
        foreach (var _ in nesting)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent).AppendLine("}");
        }
        return sb.ToString();
    }

    private static void EmitProperty(StringBuilder sb, string indent, HashSet<string> existing, string name, string type)
    {
        if (existing.Contains(name)) return;
        sb.Append(indent).Append("public ").Append(type).Append(' ').Append(name).AppendLine(" { get; set; }");
    }
}
