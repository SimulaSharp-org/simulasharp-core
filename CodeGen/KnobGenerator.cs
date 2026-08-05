using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SimulaSharp.CodeGen;

/// <summary>
/// For every partial model <b>or service</b> with knob-decorated partial properties, implements them
/// (volatile get; volatile set + change-notify) and builds a <c>Knob</c> binder
/// per knob via an explicit <c>IKnobProvider.CollectKnobs</c> the simulator calls during
/// discovery. Covers value knobs (Text/TickBox/Number&lt;T&gt;) plus trigger knobs. The MMI-facing writes and
/// triggers marshal onto the owner's thread through its dispatcher — a model's <c>Scheduler</c> or a service's
/// <c>Site</c> (the surface common to both sim-paced and loosely-coupled services).
/// </summary>
[Generator]
public sealed class KnobGenerator : IIncrementalGenerator
{
    private const string IModelFullName = "SimulaSharp.Kernel.Model.IModel";
    private const string IServiceFullName = "SimulaSharp.Kernel.Model.IService";
    private const string KnobAttrBase = "SimulaSharp.Kernel.Attributes.KnobAttribute";
    private const string TriggerAttr = "SimulaSharp.Kernel.Attributes.TriggerKnobAttribute";
    private const string K = "global::SimulaSharp.Kernel.Knobs";
    private const string Vol = "global::System.Threading.Volatile";

    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    // SIM030: [EnumKnob] on a member whose type is not an enum.
    private static readonly DiagnosticDescriptor EnumKnobNotEnum = new(
        "SIM030", "[EnumKnob] must be an enum property",
        "[EnumKnob] member '{0}' must be a property of an enum type",
        "SimulaSharp.Observability", DiagnosticSeverity.Error, isEnabledByDefault: true);

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
        bool isService = model.AllInterfaces.Any(static i => i.ToDisplayString() == IServiceFullName);
        bool isModel = model.AllInterfaces.Any(static i => i.ToDisplayString() == IModelFullName);
        if (model.IsGenericType || (!isModel && !isService))
            return null;
        var first = model.DeclaringSyntaxReferences.FirstOrDefault();
        if (first is null || first.GetSyntax() != ctx.Node) return null;

        // A model marshals MMI-facing writes/triggers through its own IScheduler; a service through its public Site
        // (IDispatcherSite) — the one dispatch surface common to both sim-paced and loosely-coupled services. Both
        // are IDispatcher, so either exposes Post.
        string dispatch = isService ? "Site" : "Scheduler";

        var diags = ImmutableArray.CreateBuilder<Diagnostic>();
        var knobs = new List<(IPropertySymbol Prop, AttributeData Attr, string Kind)>();
        var triggers = new List<(AttributeData Attr, string Name)>();
        foreach (var member in model.GetMembers())
        {
            switch (member)
            {
                case IPropertySymbol { IsPartialDefinition: true } p:
                {
                    var attr = p.GetAttributes().FirstOrDefault(static a => IsKnobAttr(a.AttributeClass));
                    string? kind = attr is null ? null : KindOf(attr.AttributeClass);
                    if (attr is null || kind is null) break;
                    // An enum (dropdown) knob must be on an enum property — otherwise the underlying-type
                    // materialization has nothing to build. Flag it (SIM030) and skip, not emit broken code.
                    if (kind == "Enum" && p.Type.TypeKind != TypeKind.Enum)
                    {
                        diags.Add(Diagnostic.Create(EnumKnobNotEnum, p.Locations.FirstOrDefault() ?? Location.None, p.Name));
                        break;
                    }
                    knobs.Add((p, attr, kind));
                    break;
                }
                // Trigger knob: a parameterless void Handle_<Name>() method.
                case IMethodSymbol { MethodKind: MethodKind.Ordinary, ReturnsVoid: true } m when m.Parameters.IsEmpty:
                {
                    var attr = m.GetAttributes().FirstOrDefault(static a => a.AttributeClass?.ToDisplayString() == TriggerAttr);
                    if (attr is not null && m.Name.StartsWith("Handle_"))
                        triggers.Add((attr, m.Name.Substring("Handle_".Length)));
                    break;
                }
            }
        }
        if (knobs.Count == 0 && triggers.Count == 0)
            return diags.Count == 0 ? null : new Result(null, null, diags.ToImmutable());

        bool hasNs = !model.ContainingNamespace.IsGlobalNamespace;
        string? ns = hasNs ? model.ContainingNamespace.ToDisplayString() : null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        if (hasNs) { sb.Append("namespace ").Append(ns).AppendLine(); sb.AppendLine("{"); }
        sb.Append("partial class ").Append(model.Name).Append(" : ").Append(K).AppendLine(".IKnobProvider");
        sb.AppendLine("{");

        // Backing fields + partial property implementations + knob fields.
        foreach (var (p, _, kind) in knobs)
        {
            string t = p.Type.ToDisplayString(Fq);
            string f = "__" + p.Name;
            bool isEnum = kind == "Enum";
            // Volatile needs a primitive, so an enum knob backs its value with the enum's underlying integer and
            // casts through it on the typed (enum) property. All other kinds back the field with the property type.
            string ft = isEnum ? Underlying(p) : t;
            sb.Append("    private ").Append(ft).Append(' ').Append(f).AppendLine(" = default!;");
            sb.Append("    private ").Append(K).Append(".Knob? ").Append(f).AppendLine("Knob;");
            sb.Append("    public partial ").Append(t).Append(' ').Append(p.Name).AppendLine();
            sb.AppendLine("    {");
            if (isEnum)
                sb.Append("        get => (").Append(t).Append(')').Append(Vol).Append(".Read(ref ").Append(f).AppendLine(");");
            else
                sb.Append("        get => ").Append(Vol).Append(".Read(ref ").Append(f).AppendLine(");");
            if (p.SetMethod is not null)
            {
                string setVal = isEnum ? "(" + ft + ")value" : "value";
                sb.Append("        set { ").Append(Vol).Append(".Write(ref ").Append(f).Append(", ").Append(setVal).Append("); ")
                  .Append(f).AppendLine("Knob?.RaiseChanged(); }");
            }
            sb.AppendLine("    }");
            // Model-side subscription for cascaded logic (fires on the site thread). Subscribe in Configure().
            sb.Append("    protected event global::System.Action ").Append(p.Name)
              .Append("Changed { add => ").Append(f).Append("Knob!.Changed += value; remove => ").Append(f).AppendLine("Knob!.Changed -= value; }");
        }

        // Trigger wrappers: a public <Name>() that runs Handle_<Name>() and raises the "triggered" notification.
        foreach (var (_, name) in triggers)
        {
            sb.Append("    private ").Append(K).Append(".Knob? __").Append(name).AppendLine("Knob;");
            sb.Append("    public void ").Append(name).Append("() { Handle_").Append(name).Append("(); __").Append(name).AppendLine("Knob?.RaiseChanged(); }");
        }

        // Discovery hook.
        sb.AppendLine();
        sb.Append("    void ").Append(K).AppendLine(".IKnobProvider.CollectKnobs(string prefix, global::System.Collections.Generic.ICollection<" + K + ".Knob> into)");
        sb.AppendLine("    {");
        foreach (var (p, attr, kind) in knobs)
            EmitKnob(sb, dispatch, p, attr, kind);
        foreach (var (attr, name) in triggers)
            EmitTrigger(sb, dispatch, name, attr);
        sb.AppendLine("    }");

        sb.AppendLine("}");
        if (hasNs) sb.AppendLine("}");

        string hint = (hasNs ? ns + "." : "") + model.Name + ".Knobs.g.cs";
        return new Result(hint, sb.ToString(), diags.ToImmutable());
    }

    private static void EmitKnob(StringBuilder sb, string dispatch, IPropertySymbol p, AttributeData attr, string kind)
    {
        string t = p.Type.ToDisplayString(Fq);
        string f = "__" + p.Name;
        bool ro = Named(attr, "IsReadOnly") is bool b && b;
        bool writable = p.SetMethod is not null && !ro;

        sb.Append("        ").Append(f).Append("Knob = new ").Append(K).AppendLine(".Knob");
        sb.AppendLine("        {");
        sb.Append("            Path = prefix + \".").Append(p.Name).AppendLine("\",");
        sb.Append("            Label = ").Append(Str(attr.ConstructorArguments.FirstOrDefault().Value as string ?? p.Name)).AppendLine(",");
        sb.Append("            Kind = ").Append(K).Append(".KnobKind.").Append(kind).AppendLine(",");
        sb.Append("            IsReadOnly = ").Append(ro ? "true" : "false").AppendLine(",");
        sb.Append("            ValueType = ").Append(Str(p.Type.ToDisplayString())).AppendLine(",");
        AppendNum(sb, "Min", Named(attr, "Min"));
        AppendNum(sb, "Max", Named(attr, "Max"));
        AppendInt(sb, "DecimalPlaces", Named(attr, "DecimalPlaces"));
        AppendInt(sb, "MaxLength", Named(attr, "MaxLength"));
        AppendBool(sb, "AllowEmpty", Named(attr, "AllowEmpty"));
        AppendBool(sb, "AllowNewLine", Named(attr, "AllowNewLine"));
        if (kind == "Enum")
        {
            // Options carry (underlying value, member name); the value is exchanged as long (matching KnobOption.Value)
            // over the enum's underlying-integer backing field.
            AppendOptions(sb, p);
            string ut = Underlying(p);
            sb.Append("            ReadBoxed = () => (long)").Append(Vol).Append(".Read(ref ").Append(f).AppendLine("),");
            if (writable)
                sb.Append("            WriteBoxed = v => { ").Append(Vol).Append(".Write(ref ").Append(f).Append(", (").Append(ut)
                  .Append(")(long)v!); ").Append(dispatch).Append(".Post(() => ").Append(f).AppendLine("Knob!.RaiseChanged()); },");
        }
        else
        {
            sb.Append("            ReadBoxed = () => ").Append(Vol).Append(".Read(ref ").Append(f).AppendLine("),");
            if (writable)
                sb.Append("            WriteBoxed = v => { ").Append(Vol).Append(".Write(ref ").Append(f).Append(", (").Append(t)
                  .Append(")v!); ").Append(dispatch).Append(".Post(() => ").Append(f).AppendLine("Knob!.RaiseChanged()); },");
        }
        sb.AppendLine("        };");
        sb.Append("        into.Add(").Append(f).AppendLine("Knob);");
    }

    /// <summary>The fully-qualified underlying integer type of an enum property (e.g. <c>int</c>).</summary>
    private static string Underlying(IPropertySymbol p) =>
        ((INamedTypeSymbol)p.Type).EnumUnderlyingType!.ToDisplayString(Fq);

    /// <summary>Emits the <c>Options</c> initializer for an enum knob: one <c>KnobOption(underlyingValue, "Name")</c> per member.</summary>
    private static void AppendOptions(StringBuilder sb, IPropertySymbol p)
    {
        sb.Append("            Options = new ").Append(K).AppendLine(".KnobOption[]");
        sb.AppendLine("            {");
        foreach (var m in ((INamedTypeSymbol)p.Type).GetMembers().OfType<IFieldSymbol>().Where(fld => fld.HasConstantValue))
            sb.Append("                new ").Append(K).Append(".KnobOption(")
              .Append(Convert.ToInt64(m.ConstantValue, CultureInfo.InvariantCulture)).Append("L, ").Append(Str(m.Name)).AppendLine("),");
        sb.AppendLine("            },");
    }

    private static void EmitTrigger(StringBuilder sb, string dispatch, string name, AttributeData attr)
    {
        bool ro = Named(attr, "IsReadOnly") is bool b && b;
        sb.Append("        __").Append(name).Append("Knob = new ").Append(K).AppendLine(".Knob");
        sb.AppendLine("        {");
        sb.Append("            Path = prefix + \".").Append(name).AppendLine("\",");
        sb.Append("            Label = ").Append(Str(attr.ConstructorArguments.FirstOrDefault().Value as string ?? name)).AppendLine(",");
        sb.Append("            Kind = ").Append(K).AppendLine(".KnobKind.Trigger,");
        sb.Append("            IsReadOnly = ").Append(ro ? "true" : "false").AppendLine(",");
        sb.Append("            Invoke = () => ").Append(dispatch).Append(".Post(() => ").Append(name).AppendLine("()),");
        sb.AppendLine("        };");
        sb.Append("        into.Add(__").Append(name).AppendLine("Knob);");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static bool IsKnobAttr(INamedTypeSymbol? c)
    {
        for (; c is not null; c = c.BaseType)
            if (c.ToDisplayString() == KnobAttrBase) return true;
        return false;
    }

    // Matches on the ORIGINAL definition so the generic NumberKnobAttribute<T> is recognized for any T
    // (its constructed display string is NumberKnobAttribute<int>, <double>, … — the open form is <T>).
    private static string? KindOf(INamedTypeSymbol? c) => c?.OriginalDefinition.ToDisplayString() switch
    {
        "SimulaSharp.Kernel.Attributes.TextKnobAttribute" => "Text",
        "SimulaSharp.Kernel.Attributes.TickBoxKnobAttribute" => "Bool",
        "SimulaSharp.Kernel.Attributes.NumberKnobAttribute<T>" => "Number",
        "SimulaSharp.Kernel.Attributes.EnumKnobAttribute" => "Enum",
        _ => null
    };

    private static object? Named(AttributeData a, string name)
    {
        foreach (var n in a.NamedArguments)
            if (n.Key == name) return n.Value.Value;
        return null;
    }

    private static void AppendNum(StringBuilder sb, string field, object? v)
    {
        if (v is null) return;
        sb.Append("            ").Append(field).Append(" = ")
          .Append(Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)).AppendLine(",");
    }

    private static void AppendInt(StringBuilder sb, string field, object? v)
    {
        if (v is null) return;
        sb.Append("            ").Append(field).Append(" = ").Append(Convert.ToInt64(v, CultureInfo.InvariantCulture)).AppendLine(",");
    }

    private static void AppendBool(StringBuilder sb, string field, object? v)
    {
        if (v is bool b) sb.Append("            ").Append(field).Append(" = ").Append(b ? "true" : "false").AppendLine(",");
    }

    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
