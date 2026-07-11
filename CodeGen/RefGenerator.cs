using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SimulaSharp.CodeGen;

/// <summary>
/// Emits the guarded reference accessors. For <b>every</b> concrete model that implements
/// <c>SimulaSharp.Kernel.Model.IModel</c> it emits a <c>&lt;Model&gt;Ref</c> class wrapping an instance and exposing
/// one async accessor per public member (methods verbatim; properties/fields as <c>Get&lt;Name&gt;</c>/
/// <c>Set&lt;Name&gt;</c>). Every accessor takes the caller's scheduler: same-site as the target it runs directly and
/// completes synchronously, otherwise it is marshalled onto the target's site via <c>InjectAsync</c> and awaited.
/// Class Refs are eager (they exist for scripting/live invocation, not just for wired references).
/// <para>
/// When an <b>interface</b> is used as a <c>[References&lt;I&gt;]</c> target, it additionally emits a companion
/// <c>&lt;I&gt;Ref</c> <b>interface</b> declaring the same accessor signatures, and makes every model Ref whose model
/// implements <c>I</c> implement <c>&lt;I&gt;Ref</c> — so an <c>IReference&lt;I Ref&gt;</c> can hold the concrete Refs
/// of any implementation polymorphically.
/// </para>
/// </summary>
[Generator]
public sealed class RefGenerator : IIncrementalGenerator
{
    private const string IModelFullName = "SimulaSharp.Kernel.Model.IModel";
    private const string ReferencesAttr = "SimulaSharp.Kernel.Attributes.ReferencesAttribute`1";
    private const string IModelRef = "global::SimulaSharp.Kernel.Model.IModelRef";
    private const string Scheduler = "global::SimulaSharp.Kernel.Services.IScheduler";
    private const string Task = "global::System.Threading.Tasks.Task";

    // Members never surfaced on a Ref: IModel/IService plumbing and object overrides.
    private static readonly HashSet<string> ExcludedNames = new()
    {
        "Configure", "Dispose", "TimeKeeper", "Scheduler", "Logger",
        "ToString", "Equals", "GetHashCode"
    };

    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The interfaces used as [References<I>] targets, collected once across the compilation.
        var refInterfaces = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ReferencesAttr,
                static (_, _) => true,
                static (ctx, _) => InterfaceTargets(ctx))
            .SelectMany(static (xs, _) => xs)
            .Collect();

        // 1. Companion Ref interfaces — one per distinct reference-target interface.
        context.RegisterSourceOutput(refInterfaces, static (spc, ifaces) => EmitCompanions(spc, ifaces));

        // 2. Class Refs — every concrete IModel class, implementing the companion interfaces it qualifies for.
        var models = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, _) => Model(ctx))
            .Where(static m => m is not null);

        context.RegisterSourceOutput(models.Combine(refInterfaces),
            static (spc, pair) => EmitClassRef(spc, pair.Left!, pair.Right));
    }

    /// <summary>The interface type arguments of the <c>[References&lt;I&gt;]</c> attributes on one class.</summary>
    private static ImmutableArray<INamedTypeSymbol> InterfaceTargets(GeneratorAttributeSyntaxContext ctx)
    {
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var a in ctx.Attributes)
            if (a.AttributeClass is { TypeArguments.Length: 1 } ac
                && ac.TypeArguments[0] is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface)
                result.Add(iface);
        return result.ToImmutable();
    }

    /// <summary>The concrete, non-generic <c>IModel</c> class declared by this node, or null.</summary>
    private static INamedTypeSymbol? Model(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is not INamedTypeSymbol model)
            return null;
        if (model.IsAbstract || model.IsStatic || model.IsGenericType) return null;
        if (!model.AllInterfaces.Any(static i => i.ToDisplayString() == IModelFullName)) return null;
        return model;
    }

    // ── Companion Ref interfaces ────────────────────────────────────────────────

    private static void EmitCompanions(SourceProductionContext spc, ImmutableArray<INamedTypeSymbol> ifaces)
    {
        var seen = new HashSet<string>();
        foreach (var iface in ifaces)
        {
            if (!seen.Add(iface.ToDisplayString(Fq))) continue;
            var (hint, source) = BuildCompanion(iface);
            spc.AddSource(hint, source);
        }
    }

    private static (string HintName, string Source) BuildCompanion(INamedTypeSymbol iface)
    {
        string refName = iface.Name + "Ref";
        string access = iface.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        bool hasNs = !iface.ContainingNamespace.IsGlobalNamespace;
        string? ns = hasNs ? iface.ContainingNamespace.ToDisplayString() : null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        if (hasNs) { sb.Append("namespace ").Append(ns).AppendLine(); sb.AppendLine("{"); }
        sb.Append(access).Append(" interface ").Append(refName).Append(" : ").AppendLine(IModelRef);
        sb.AppendLine("{");

        // The interface's own members plus everything it inherits — same filter as the class Ref, so the
        // signatures line up and the class Ref satisfies this interface member-for-member.
        var seen = new HashSet<string>();
        foreach (var t in new[] { iface }.Concat(iface.AllInterfaces))
            foreach (var member in t.GetMembers())
                EmitSignature(sb, member, seen);

        sb.AppendLine("}");
        if (hasNs) sb.AppendLine("}");

        string hint = (hasNs ? ns + "." : "") + refName + ".g.cs";
        return (hint, sb.ToString());
    }

    /// <summary>Appends an accessor <em>signature</em> (declaration only) for one interface member.</summary>
    private static void EmitSignature(StringBuilder sb, ISymbol member, HashSet<string> seen)
    {
        if (member.IsStatic || ExcludedNames.Contains(member.Name)) return;
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Ordinary } m when CanEmit(m):
                if (seen.Add(MethodKey(m)))
                {
                    var (decls, _) = Parameters(m.Parameters);
                    string sig = m.ReturnsVoid
                        ? VoidSig(m.Name, decls)
                        : ValueSig(m.Name, m.ReturnType.ToDisplayString(Fq), decls);
                    sb.Append("    ").Append(sig).AppendLine(";");
                }
                break;
            case IPropertySymbol { IsIndexer: false } p:
                if (seen.Add("P:" + p.Name))
                {
                    string type = p.Type.ToDisplayString(Fq);
                    if (p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
                        sb.Append("    ").Append(ValueSig("Get" + p.Name, type, "")).AppendLine(";");
                    if (p.SetMethod is { DeclaredAccessibility: Accessibility.Public })
                        sb.Append("    ").Append(VoidSig("Set" + p.Name, ", " + type + " value")).AppendLine(";");
                }
                break;
        }
    }

    // ── Class Refs ──────────────────────────────────────────────────────────────

    private static void EmitClassRef(SourceProductionContext spc, INamedTypeSymbol model,
        ImmutableArray<INamedTypeSymbol> refInterfaces)
    {
        string target = model.ToDisplayString(Fq);
        string access = model.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        string refName = model.Name + "Ref";
        bool hasNamespace = !model.ContainingNamespace.IsGlobalNamespace;
        string? ns = hasNamespace ? model.ContainingNamespace.ToDisplayString() : null;

        // Companion interfaces this model qualifies for: a reference-target interface it implements.
        var bases = new List<string> { IModelRef };
        var seenIface = new HashSet<string>();
        foreach (var iface in refInterfaces)
        {
            if (!seenIface.Add(iface.ToDisplayString(Fq))) continue;
            if (model.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iface)))
                bases.Add(iface.ToDisplayString(Fq) + "Ref");
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        if (hasNamespace) { sb.Append("namespace ").Append(ns).AppendLine(); sb.AppendLine("{"); }

        sb.Append(access).Append(" sealed class ").Append(refName).Append(" : ").AppendLine(string.Join(", ", bases));
        sb.AppendLine("{");
        sb.Append("    private readonly ").Append(target).AppendLine(" _target;");
        sb.Append("    public ").Append(refName).Append('(').Append(target).AppendLine(" target) => _target = target;");

        // Walk the model and every base type (up to but not including object), so inherited public members are
        // routed too. Overrides/shadows are emitted once (most-derived wins, since we walk derived -> base).
        var seen = new HashSet<string>();
        for (INamedTypeSymbol? t = model; t is { SpecialType: not SpecialType.System_Object }; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic) continue;
                if (ExcludedNames.Contains(member.Name)) continue;

                switch (member)
                {
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary } m when CanEmit(m):
                        if (seen.Add(MethodKey(m))) EmitMethod(sb, m);
                        break;
                    case IPropertySymbol { IsIndexer: false } p:
                        if (seen.Add("P:" + p.Name)) EmitProperty(sb, p);
                        break;
                    case IFieldSymbol { IsConst: false, IsImplicitlyDeclared: false } f:
                        if (seen.Add("F:" + f.Name)) EmitField(sb, f);
                        break;
                }
            }
        }

        sb.AppendLine("}");
        if (hasNamespace) sb.AppendLine("}");

        string hint = (hasNamespace ? ns + "." : "") + refName + ".g.cs";
        spc.AddSource(hint, sb.ToString());
    }

    // ── Shared member emission ──────────────────────────────────────────────────

    // v1 skips generic methods and by-ref parameters (can't be captured in the inject lambda).
    private static bool CanEmit(IMethodSymbol m) =>
        !m.IsGenericMethod && m.Parameters.All(static p => p.RefKind == RefKind.None);

    // Dedupe key: name + parameter types, so overloads stay distinct but overrides collapse.
    private static string MethodKey(IMethodSymbol m) =>
        "M:" + m.Name + "(" + string.Join(",", m.Parameters.Select(static p => p.Type.ToDisplayString(Fq))) + ")";

    private static string ValueSig(string name, string type, string extraDecls) =>
        Task + "<" + type + "> " + name + "(" + Scheduler + " callerScheduler" + extraDecls + ")";

    private static string VoidSig(string name, string extraDecls) =>
        Task + " " + name + "(" + Scheduler + " callerScheduler" + extraDecls + ")";

    private static void EmitMethod(StringBuilder sb, IMethodSymbol m)
    {
        var (decls, args) = Parameters(m.Parameters);
        string invoke = "_target." + m.Name + "(" + args + ")";
        if (m.ReturnsVoid) EmitVoid(sb, m.Name, decls, invoke);
        else EmitValue(sb, m.Name, m.ReturnType.ToDisplayString(Fq), decls, invoke);
    }

    private static void EmitProperty(StringBuilder sb, IPropertySymbol p)
    {
        string type = p.Type.ToDisplayString(Fq);
        if (p.GetMethod is { DeclaredAccessibility: Accessibility.Public })
            EmitValue(sb, "Get" + p.Name, type, "", "_target." + p.Name);
        if (p.SetMethod is { DeclaredAccessibility: Accessibility.Public })
            EmitVoid(sb, "Set" + p.Name, ", " + type + " value", "_target." + p.Name + " = value");
    }

    private static void EmitField(StringBuilder sb, IFieldSymbol f)
    {
        string type = f.Type.ToDisplayString(Fq);
        EmitValue(sb, "Get" + f.Name, type, "", "_target." + f.Name);
        if (!f.IsReadOnly)
            EmitVoid(sb, "Set" + f.Name, ", " + type + " value", "_target." + f.Name + " = value");
    }

    /// <summary>Emits a value-returning accessor: same-site direct, else marshalled via InjectAsync.</summary>
    private static void EmitValue(StringBuilder sb, string name, string type, string extraDecls, string valueExpr)
    {
        sb.AppendLine();
        sb.Append("    public ").AppendLine(ValueSig(name, type, extraDecls));
        sb.AppendLine("    {");
        sb.AppendLine("        if (object.ReferenceEquals(callerScheduler, _target.Scheduler))");
        sb.Append("            return ").Append(Task).Append(".FromResult(").Append(valueExpr).AppendLine(");");
        sb.Append("        return _target.Scheduler.InjectAsync(() => ").Append(valueExpr).AppendLine(");");
        sb.AppendLine("    }");
    }

    /// <summary>Emits a void accessor: same-site direct, else marshalled via InjectVoidAsync.</summary>
    private static void EmitVoid(StringBuilder sb, string name, string extraDecls, string stmtExpr)
    {
        sb.AppendLine();
        sb.Append("    public ").AppendLine(VoidSig(name, extraDecls));
        sb.AppendLine("    {");
        sb.AppendLine("        if (object.ReferenceEquals(callerScheduler, _target.Scheduler))");
        sb.AppendLine("        {");
        sb.Append("            ").Append(stmtExpr).AppendLine(";");
        sb.Append("            return ").Append(Task).AppendLine(".CompletedTask;");
        sb.AppendLine("        }");
        sb.Append("        return _target.Scheduler.InjectVoidAsync(() => ").Append(stmtExpr).AppendLine(");");
        sb.AppendLine("    }");
    }

    /// <summary>Renders parameter declarations (with a leading comma) and the matching argument list.</summary>
    private static (string Decls, string Args) Parameters(IReadOnlyList<IParameterSymbol> ps)
    {
        if (ps.Count == 0) return ("", "");
        var decls = new StringBuilder();
        var args = new StringBuilder();
        for (int i = 0; i < ps.Count; i++)
        {
            decls.Append(", ").Append(ps[i].Type.ToDisplayString(Fq)).Append(' ').Append(ps[i].Name);
            if (i > 0) args.Append(", ");
            args.Append(ps[i].Name);
        }
        return (decls.ToString(), args.ToString());
    }
}
