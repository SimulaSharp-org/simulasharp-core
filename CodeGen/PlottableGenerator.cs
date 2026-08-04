using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SimulaSharp.CodeGen;

/// <summary>
/// For every partial model <b>or service</b> with <c>[Plottable]</c> properties, implements
/// <c>IPlottableProvider.CollectPlottables</c> — building a <c>Plottable</c>
/// binder per member whose <c>Sample</c> delegate does a volatile read (through the
/// member's volatile getter) projected to <c>double</c>. Sampling is lock-free (the MMI reads on its own
/// thread), so — unlike knobs — nothing here dispatches onto the owner's thread.
/// <para>
/// Materialization is coordinated with <see cref="KnobGenerator"/>: a plottable property
/// that is <em>also</em> a knob is already materialized (volatile backing field + partial
/// get/set) by the knob generator, so here we only reference it. A plottable-only partial
/// property is materialized here (volatile get + set, no change-notify — plottable is
/// observe-only). Either way the sampler reads the same volatile getter.
/// </para>
/// </summary>
[Generator]
public sealed class PlottableGenerator : IIncrementalGenerator
{
    private const string IModelFullName = "SimulaSharp.Kernel.Model.IModel";
    private const string IServiceFullName = "SimulaSharp.Kernel.Model.IService";
    private const string PlottableAttr = "SimulaSharp.Kernel.Attributes.PlottableAttribute";
    private const string KnobAttrBase = "SimulaSharp.Kernel.Attributes.KnobAttribute";
    private const string P = "global::SimulaSharp.Kernel.Plotting";
    private const string Vol = "global::System.Threading.Volatile";

    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    // SIM020: a [Plottable] member whose type can't be projected to double.
    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "SIM020", "Unsupported plottable type",
        "[Plottable] member '{0}' must be a readable numeric, bool, or enum property",
        "SimulaSharp.Plotting", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (n, _) => n is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword),
                transform: static (ctx, _) => Build(ctx))
            .Where(static g => g is not null)
            .Select(static (g, _) => g!.Value);

        context.RegisterSourceOutput(models, static (spc, g) =>
        {
            foreach (var d in g.Diagnostics) spc.ReportDiagnostic(d);
            if (g.Source is not null) spc.AddSource(g.HintName!, g.Source);
        });
    }

    private readonly struct Result(string? hintName, string? source, ImmutableArray<Diagnostic> diagnostics)
    {
        public readonly string? HintName = hintName;
        public readonly string? Source = source;
        public readonly ImmutableArray<Diagnostic> Diagnostics = diagnostics;
    }

    private static Result? Build(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is not INamedTypeSymbol model)
            return null;
        bool isComponent = model.AllInterfaces.Any(static i => i.ToDisplayString() is IModelFullName or IServiceFullName);
        if (model.IsGenericType || !isComponent)
            return null;
        // Emit once per type even if it is declared across multiple partial files.
        var first = model.DeclaringSyntaxReferences.FirstOrDefault();
        if (first is null || first.GetSyntax() != ctx.Node) return null;

        var props = new List<IPropertySymbol>();
        foreach (var member in model.GetMembers())
            if (member is IPropertySymbol p && p.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == PlottableAttr))
                props.Add(p);
        if (props.Count == 0) return null;

        var diags = ImmutableArray.CreateBuilder<Diagnostic>();
        var plot = new List<(IPropertySymbol Prop, AttributeData Attr, string Expr, bool Materialize)>();
        foreach (var p in props)
        {
            string? expr = p.GetMethod is null ? null : SampleExpr(p);
            if (expr is null)
            {
                diags.Add(Diagnostic.Create(UnsupportedType, p.Locations.FirstOrDefault(), p.Name));
                continue;
            }
            var attr = p.GetAttributes().First(static a => a.AttributeClass?.ToDisplayString() == PlottableAttr);
            bool hasKnob = p.GetAttributes().Any(static a => IsKnobAttr(a.AttributeClass));
            // Materialize the volatile partial ourselves only when it is a partial definition
            // and NOT a knob (the knob generator materializes the shared field otherwise).
            bool materialize = p.IsPartialDefinition && !hasKnob;
            plot.Add((p, attr, expr, materialize));
        }
        if (plot.Count == 0)
            return new Result(null, null, diags.ToImmutable());

        bool hasNs = !model.ContainingNamespace.IsGlobalNamespace;
        string? ns = hasNs ? model.ContainingNamespace.ToDisplayString() : null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        if (hasNs) { sb.Append("namespace ").Append(ns).AppendLine(); sb.AppendLine("{"); }
        sb.Append("partial class ").Append(model.Name).Append(" : ").Append(P).AppendLine(".IPlottableProvider");
        sb.AppendLine("{");

        // Materialize plottable-only partial properties (volatile get + optional volatile set).
        foreach (var (p, _, _, materialize) in plot)
        {
            if (!materialize) continue;
            string t = p.Type.ToDisplayString(Fq);
            string f = "__" + p.Name;
            sb.Append("    private ").Append(t).Append(' ').Append(f).AppendLine(" = default!;");
            sb.Append("    public partial ").Append(t).Append(' ').Append(p.Name).AppendLine();
            sb.AppendLine("    {");
            sb.Append("        get => ").Append(Vol).Append(".Read(ref ").Append(f).AppendLine(");");
            if (p.SetMethod is not null)
                sb.Append("        set => ").Append(Vol).Append(".Write(ref ").Append(f).AppendLine(", value);");
            sb.AppendLine("    }");
        }

        // Discovery hook.
        sb.AppendLine();
        sb.Append("    void ").Append(P).AppendLine(".IPlottableProvider.CollectPlottables(string prefix, global::System.Collections.Generic.IDictionary<string, " + P + ".Plottable> into)");
        sb.AppendLine("    {");
        foreach (var (p, attr, expr, _) in plot)
        {
            string label = attr.ConstructorArguments.FirstOrDefault().Value as string ?? p.Name;
            string? unit = Named(attr, "Unit") as string;
            string labelOverride = Named(attr, "Label") as string ?? label;
            sb.Append("        into[prefix + \".").Append(p.Name).Append("\"] = new ").Append(P).AppendLine(".Plottable");
            sb.AppendLine("        {");
            sb.Append("            Path = prefix + \".").Append(p.Name).AppendLine("\",");
            sb.Append("            Label = ").Append(Str(labelOverride)).AppendLine(",");
            sb.Append("            Unit = ").Append(unit is null ? "null" : Str(unit)).AppendLine(",");
            sb.Append("            ValueType = ").Append(Str(p.Type.ToDisplayString())).AppendLine(",");
            sb.Append("            Sample = () => ").Append(expr).AppendLine(",");
            sb.AppendLine("        };");
        }
        sb.AppendLine("    }");

        sb.AppendLine("}");
        if (hasNs) sb.AppendLine("}");

        string hint = (hasNs ? ns + "." : "") + model.Name + ".Plottables.g.cs";
        return new Result(hint, sb.ToString(), diags.ToImmutable());
    }

    /// <summary>Projects a supported member type to a <c>double</c> read expression, or null if unsupported.</summary>
    private static string? SampleExpr(IPropertySymbol p)
    {
        string name = "this." + p.Name;
        if (p.Type.TypeKind == TypeKind.Enum && p.Type is INamedTypeSymbol en && en.EnumUnderlyingType is { } u)
            return "(double)(" + u.ToDisplayString(Fq) + ")" + name;
        return p.Type.SpecialType switch
        {
            SpecialType.System_Boolean => "(" + name + " ? 1.0 : 0.0)",
            SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Decimal => "(double)" + name,
            _ => null
        };
    }

    private static bool IsKnobAttr(INamedTypeSymbol? c)
    {
        for (; c is not null; c = c.BaseType)
            if (c.ToDisplayString() == KnobAttrBase) return true;
        return false;
    }

    private static object? Named(AttributeData a, string name)
    {
        foreach (var n in a.NamedArguments)
            if (n.Key == name) return n.Value.Value;
        return null;
    }

    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
