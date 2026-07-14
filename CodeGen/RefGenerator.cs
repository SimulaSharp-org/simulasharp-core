using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SimulaSharp.CodeGen;

/// <summary>
/// Emits the guarded cross-site reference wrappers. For each <b>interface</b> <c>I</c> used as a
/// <c>[CrossReference&lt;I&gt;]</c> target it emits a single <c>sealed class &lt;I&gt;Ref : IModelRef</c> that wraps an
/// <c>I</c> target and the two schedulers captured at construction (the target's and the caller's), then routes each of
/// <c>I</c>'s members by a three-tier taxonomy:
/// <list type="bullet">
///   <item><b><c>[Triggerable]</c></b> <c>void</c> methods → fire-and-forget: same-site inline, cross-site <c>Post</c>.</item>
///   <item><b><c>[VolatileAccess]</c></b> get-only properties → a plain synchronous getter (<c>=&gt; _target.P</c>), no dispatch.</item>
///   <item>everything else → async request/response: same-site inline + completed <c>Task</c>, cross-site <c>InjectAsync</c>/<c>InjectVoidAsync</c>.</item>
/// </list>
/// Because a cross-reference target is always the interface, there is exactly one Ref per interface — no per-model Ref
/// classes and no companion Ref interfaces (the target opacity makes them unnecessary).
/// </summary>
[Generator]
public sealed class RefGenerator : IIncrementalGenerator
{
    private const string CrossRefAttr = "SimulaSharp.Kernel.Attributes.CrossReferenceAttribute`1";
    private const string TriggerableAttr = "SimulaSharp.Kernel.Attributes.TriggerableAttribute";
    private const string VolatileAccessAttr = "SimulaSharp.Kernel.Attributes.VolatileAccessAttribute";
    private const string IModelRef = "global::SimulaSharp.Kernel.Model.IModelRef";
    private const string Scheduler = "global::SimulaSharp.Kernel.Services.IScheduler";
    private const string Task = "global::System.Threading.Tasks.Task";

    // Members never surfaced on a Ref: IModel/IService plumbing and object overrides (in case a capability interface
    // happens to derive one of them).
    private static readonly HashSet<string> ExcludedNames = new()
    {
        "Configure", "Dispose", "TimeKeeper", "Scheduler", "Logger",
        "ToString", "Equals", "GetHashCode"
    };

    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly DiagnosticDescriptor TriggerableNotVoid = new("SIM019", "[Triggerable] must be void",
        "[Triggerable] method '{0}' must return void — a fire-and-forget call has no result to await", "SimulaSharp.Assembly",
        DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor VolatileAccessNotGauge = new("SIM021", "[VolatileAccess] must be a get-only gauge",
        "[VolatileAccess] property '{0}' must be a get-only property of an atomically-readable type (a word-size primitive, enum, or reference)",
        "SimulaSharp.Assembly", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The interfaces used as [CrossReference<I>] targets, collected once across the compilation.
        var refInterfaces = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                CrossRefAttr,
                static (_, _) => true,
                static (ctx, _) => InterfaceTargets(ctx))
            .SelectMany(static (xs, _) => xs)
            .Collect();

        context.RegisterSourceOutput(refInterfaces, static (spc, ifaces) => EmitRefs(spc, ifaces));
    }

    /// <summary>The interface type arguments of the <c>[CrossReference&lt;I&gt;]</c> attributes on one class.</summary>
    private static ImmutableArray<INamedTypeSymbol> InterfaceTargets(GeneratorAttributeSyntaxContext ctx)
    {
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var a in ctx.Attributes)
            if (a.AttributeClass is { TypeArguments.Length: 1 } ac
                && ac.TypeArguments[0] is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface)
                result.Add(iface);
        return result.ToImmutable();
    }

    private static void EmitRefs(SourceProductionContext spc, ImmutableArray<INamedTypeSymbol> ifaces)
    {
        var seen = new HashSet<string>();
        foreach (var iface in ifaces)
        {
            if (!seen.Add(iface.ToDisplayString(Fq))) continue;
            var (hint, source) = BuildRef(iface, spc);
            spc.AddSource(hint, source);
        }
    }

    private static (string HintName, string Source) BuildRef(INamedTypeSymbol iface, SourceProductionContext spc)
    {
        string target = iface.ToDisplayString(Fq);
        string refName = iface.Name + "Ref";
        string access = iface.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        bool hasNs = !iface.ContainingNamespace.IsGlobalNamespace;
        string? ns = hasNs ? iface.ContainingNamespace.ToDisplayString() : null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        if (hasNs) { sb.Append("namespace ").Append(ns).AppendLine(); sb.AppendLine("{"); }

        sb.Append(access).Append(" sealed class ").Append(refName).Append(" : ").AppendLine(IModelRef);
        sb.AppendLine("{");
        sb.Append("    private readonly ").Append(target).AppendLine(" _target;");
        sb.Append("    private readonly ").Append(Scheduler).AppendLine(" _targetSched;");
        sb.Append("    private readonly ").Append(Scheduler).AppendLine(" _callerSched;");
        sb.Append("    public ").Append(refName).Append('(').Append(target).Append(" target, ")
          .Append(Scheduler).Append(" targetSched, ").Append(Scheduler).AppendLine(" callerSched)");
        sb.AppendLine("    { _target = target; _targetSched = targetSched; _callerSched = callerSched; }");

        // The interface's own members plus everything it inherits — each routed by its tier.
        var seen = new HashSet<string>();
        foreach (var t in new[] { iface }.Concat(iface.AllInterfaces))
            foreach (var member in t.GetMembers())
                EmitMember(sb, member, seen, spc);

        sb.AppendLine("}");
        if (hasNs) sb.AppendLine("}");

        string hint = (hasNs ? ns + "." : "") + refName + ".g.cs";
        return (hint, sb.ToString());
    }

    private static void EmitMember(StringBuilder sb, ISymbol member, HashSet<string> seen, SourceProductionContext spc)
    {
        if (member.IsStatic || ExcludedNames.Contains(member.Name)) return;
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Ordinary } m when CanEmit(m):
                if (!seen.Add(MethodKey(m))) return;
                var (decls, args) = Params(m.Parameters);
                string invoke = "_target." + m.Name + "(" + args + ")";
                bool triggerable = HasAttr(m, TriggerableAttr);
                if (triggerable && !m.ReturnsVoid) Report(spc, TriggerableNotVoid, m, m.Name);
                // [Triggerable] void method → fire-and-forget void; a misused (non-void) one falls back to the default.
                if (triggerable && m.ReturnsVoid) EmitTrigger(sb, m.Name, decls, invoke);
                else if (m.ReturnsVoid) EmitVoid(sb, m.Name, decls, invoke);
                else EmitValue(sb, m.Name, m.ReturnType.ToDisplayString(Fq), decls, invoke);
                break;

            case IPropertySymbol { IsIndexer: false } p:
                if (!seen.Add("P:" + p.Name)) return;
                string type = p.Type.ToDisplayString(Fq);
                bool getOnly = p.GetMethod is not null && p.SetMethod is null;
                bool gauge = HasAttr(p, VolatileAccessAttr);
                if (gauge && !(getOnly && IsAtomicallyReadable(p.Type))) Report(spc, VolatileAccessNotGauge, p, p.Name);
                // [VolatileAccess] get-only property → a plain synchronous getter (a live gauge); else the async form.
                if (gauge && getOnly && IsAtomicallyReadable(p.Type))
                {
                    sb.AppendLine();
                    sb.Append("    public ").Append(type).Append(' ').Append(p.Name).Append(" => _target.").Append(p.Name).AppendLine(";");
                }
                else
                {
                    if (p.GetMethod is not null) EmitValue(sb, "Get" + p.Name, type, "", "_target." + p.Name);
                    if (p.SetMethod is not null) EmitVoid(sb, "Set" + p.Name, type + " value", "_target." + p.Name + " = value");
                }
                break;
        }
    }

    private static void Report(SourceProductionContext spc, DiagnosticDescriptor d, ISymbol member, string arg) =>
        spc.ReportDiagnostic(Diagnostic.Create(d, member.Locations.FirstOrDefault() ?? Location.None, arg));

    /// <summary>Whether a value of <paramref name="type"/> can be read atomically across threads (no tearing): a
    /// reference type, an enum, or a word-size primitive. A non-primitive struct can tear under a bare read.</summary>
    private static bool IsAtomicallyReadable(ITypeSymbol type)
    {
        if (type.IsReferenceType || type.TypeKind == TypeKind.Enum) return true;
        return type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char
            or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
    }

    // ── Dispatch emission (schedulers captured at construction, so no per-call scheduler argument) ───────────────

    /// <summary>A value-returning request/response accessor: same-site inline + completed Task, else <c>InjectAsync</c>.</summary>
    private static void EmitValue(StringBuilder sb, string name, string type, string decls, string valueExpr)
    {
        sb.AppendLine();
        sb.Append("    public ").Append(Task).Append('<').Append(type).Append("> ").Append(name).Append('(').Append(decls).AppendLine(")");
        sb.AppendLine("    {");
        sb.AppendLine("        if (object.ReferenceEquals(_callerSched, _targetSched))");
        sb.Append("            return ").Append(Task).Append(".FromResult(").Append(valueExpr).AppendLine(");");
        sb.Append("        return _targetSched.InjectAsync(() => ").Append(valueExpr).AppendLine(");");
        sb.AppendLine("    }");
    }

    /// <summary>A void request/response accessor: same-site inline + completed Task, else <c>InjectVoidAsync</c>.</summary>
    private static void EmitVoid(StringBuilder sb, string name, string decls, string stmtExpr)
    {
        sb.AppendLine();
        sb.Append("    public ").Append(Task).Append(' ').Append(name).Append('(').Append(decls).AppendLine(")");
        sb.AppendLine("    {");
        sb.AppendLine("        if (object.ReferenceEquals(_callerSched, _targetSched))");
        sb.AppendLine("        {");
        sb.Append("            ").Append(stmtExpr).AppendLine(";");
        sb.Append("            return ").Append(Task).AppendLine(".CompletedTask;");
        sb.AppendLine("        }");
        sb.Append("        return _targetSched.InjectVoidAsync(() => ").Append(stmtExpr).AppendLine(");");
        sb.AppendLine("    }");
    }

    /// <summary>A fire-and-forget accessor: same-site inline call, else <c>Post</c> onto the target's dispatcher.</summary>
    private static void EmitTrigger(StringBuilder sb, string name, string decls, string invoke)
    {
        sb.AppendLine();
        sb.Append("    public void ").Append(name).Append('(').Append(decls).AppendLine(")");
        sb.AppendLine("    {");
        sb.Append("        if (object.ReferenceEquals(_callerSched, _targetSched)) ").Append(invoke).AppendLine(";");
        sb.Append("        else _targetSched.Post(() => ").Append(invoke).AppendLine(");");
        sb.AppendLine("    }");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Skip generic methods and by-ref parameters (can't be captured in the inject/post lambda).
    private static bool CanEmit(IMethodSymbol m) =>
        !m.IsGenericMethod && m.Parameters.All(static p => p.RefKind == RefKind.None);

    // Dedupe key: name + parameter types, so overloads stay distinct but inherited re-declarations collapse.
    private static string MethodKey(IMethodSymbol m) =>
        "M:" + m.Name + "(" + string.Join(",", m.Parameters.Select(static p => p.Type.ToDisplayString(Fq))) + ")";

    private static bool HasAttr(ISymbol s, string fqAttrName) =>
        s.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fqAttrName);

    /// <summary>Renders parameter declarations (no leading comma) and the matching argument list.</summary>
    private static (string Decls, string Args) Params(IReadOnlyList<IParameterSymbol> ps)
    {
        if (ps.Count == 0) return ("", "");
        var decls = new StringBuilder();
        var args = new StringBuilder();
        for (int i = 0; i < ps.Count; i++)
        {
            if (i > 0) { decls.Append(", "); args.Append(", "); }
            decls.Append(ps[i].Type.ToDisplayString(Fq)).Append(' ').Append(ps[i].Name);
            args.Append(ps[i].Name);
        }
        return (decls.ToString(), args.ToString());
    }
}
