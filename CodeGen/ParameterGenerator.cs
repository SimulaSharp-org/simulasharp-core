using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SimulaSharp.CodeGen;

/// <summary>
/// For every partial model/service with <c>[Parameter]</c>-marked members, emits two things into the partial:
/// <list type="bullet">
///   <item>an internal <c>__ApplyParameters(IReadOnlyDictionary&lt;string, string?&gt;)</c> that binds the
///   assembly's (weakly-typed, textual) <c>Config</c> values into the strongly-typed members — when a key is
///   present it is converted and assigned; when it is absent the attribute's <b>default</b> is applied (or, for a
///   <c>Required</c> parameter, an error is thrown);</item>
///   <item>an explicit <c>IParameterProvider.CollectParameters</c> that builds one <c>ParameterInfo</c> binder
///   per parameter (boxed read/write over the member), so the simulator can expose <c>GetParams()</c>/<c>SetParam()</c>.</item>
/// </list>
/// The generated simulator calls both while assembling (in its constructor), before <c>Configure()</c>.
/// A parameter may also carry <c>[Knob]</c>/<c>[Plottable]</c>; this generator only references the member, so it
/// composes with the field the knob generator materializes.
/// </summary>
[Generator]
public sealed class ParameterGenerator : IIncrementalGenerator
{
    private const string IModelFullName = "SimulaSharp.Kernel.Model.IModel";
    private const string IServiceFullName = "SimulaSharp.Kernel.Model.IService";
    private const string ParamAttr = "SimulaSharp.Kernel.Attributes.ParameterAttribute";
    private const string P = "global::SimulaSharp.Kernel.Parameters";

    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly DiagnosticDescriptor MustBePartial = new("SIM029",
        "Model or service must be partial",
        "'{0}' implements {1} and must be declared 'partial' so the framework can generate its code",
        "SimulaSharp.Assembly", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var components = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword),
                transform: static (ctx, _) => Build(ctx))
            .Where(static g => g is not null)
            .Select(static (g, _) => g!.Value);

        context.RegisterSourceOutput(components, static (spc, g) => spc.AddSource(g.HintName, g.Source));

        // Every model/service must be `partial`: the generated simulator calls the generated __ApplyParameters on
        // each one unconditionally (so attribute defaults apply even with no Config block), which means the method
        // must exist. A non-partial model/service could never receive one, so flag it directly (SIM029) rather
        // than letting it surface downstream as an opaque "no definition for __ApplyParameters".
        var nonPartial = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax c && !c.Modifiers.Any(SyntaxKind.PartialKeyword),
                transform: static (ctx, _) => PartialViolation(ctx))
            .Where(static v => v is not null)
            .Select(static (v, _) => v!.Value);

        context.RegisterSourceOutput(nonPartial, static (spc, v) =>
            spc.ReportDiagnostic(Diagnostic.Create(MustBePartial, v.Location, v.Name, v.Kind)));
    }

    private readonly struct Param(ISymbol member, ITypeSymbol type, string key, bool required, TypedConstant? deflt, string convert)
    {
        public ISymbol Member { get; } = member;
        public ITypeSymbol Type { get; } = type;
        public string Key { get; } = key;
        public bool Required { get; } = required;
        public TypedConstant? Default { get; } = deflt;
        public string Convert { get; } = convert;
    }

    private static (string HintName, string Source)? Build(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is not INamedTypeSymbol type)
            return null;
        if (type.IsGenericType) return null;
        if (!type.AllInterfaces.Any(static i => i.ToDisplayString() is IModelFullName or IServiceFullName)) return null;

        // Emit once per type even with several partial parts (first declaration wins).
        var first = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (first is null || first.GetSyntax() != ctx.Node) return null;

        var ps = CollectParams(type);

        // Emit a (possibly empty) parameter provider so the simulator's unconditional __ApplyParameters call
        // always resolves. The single exception: a parameterless type whose base already declares [Parameter]s
        // inherits the base's generated provider — re-emitting an empty pair here would shadow it and silently
        // drop the base's defaults, so emit nothing and let the inherited members stand.
        if (ps.Count == 0 && BaseChainHasParams(type)) return null;

        bool hasNamespace = !type.ContainingNamespace.IsGlobalNamespace;
        string? ns = hasNamespace ? type.ContainingNamespace.ToDisplayString() : null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        if (hasNamespace) { sb.Append("namespace ").Append(ns).AppendLine(); sb.AppendLine("{"); }
        sb.Append("partial class ").Append(type.Name).Append(" : ").Append(P).AppendLine(".IParameterProvider");
        sb.AppendLine("{");

        sb.AppendLine("    internal void __ApplyParameters(global::System.Collections.Generic.IReadOnlyDictionary<string, string?> config)");
        sb.AppendLine("    {");
        foreach (var p in ps) EmitApply(sb, type.Name, p);
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.Append("    void ").Append(P).AppendLine(".IParameterProvider.CollectParameters(string prefix, global::System.Collections.Generic.ICollection<" + P + ".ParameterInfo> into)");
        sb.AppendLine("    {");
        foreach (var p in ps) EmitCollect(sb, p);
        sb.AppendLine("    }");

        sb.AppendLine("}");
        if (hasNamespace) sb.AppendLine("}");

        string hint = (hasNamespace ? ns + "." : "") + type.Name + ".Parameters.g.cs";
        return (hint, sb.ToString());
    }

    /// <summary>The <c>[Parameter]</c>-annotated, settable, convertible members declared directly on <paramref name="type"/>.</summary>
    private static List<Param> CollectParams(INamedTypeSymbol type)
    {
        var ps = new List<Param>();
        foreach (var member in type.GetMembers())
        {
            var attr = member.GetAttributes().FirstOrDefault(static a => a.AttributeClass?.ToDisplayString() == ParamAttr);
            if (attr is null) continue;
            if (!Settable(member, out var mt)) continue;            // skip get-only/init/readonly/const
            string convert = ConvertExpr(mt, "__value");
            if (convert.Length == 0) continue;                       // skip unsupported (non-value) types

            string key = Named(attr, "Key") as string ?? member.Name;
            bool required = Named(attr, "Required") is true;
            TypedConstant? deflt = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is not null
                ? attr.ConstructorArguments[0] : null;
            ps.Add(new Param(member, mt, key, required, deflt, convert));
        }
        return ps;
    }

    /// <summary>True if any base type in the chain declares a generatable <c>[Parameter]</c> member — so this type
    /// inherits a generated <c>__ApplyParameters</c> and must not shadow it with an empty one.</summary>
    private static bool BaseChainHasParams(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? b = type.BaseType; b is not null; b = b.BaseType)
            if (CollectParams(b).Count > 0) return true;
        return false;
    }

    /// <summary>
    /// For a non-partial class, reports whether it implements <c>IModel</c>/<c>IService</c> (and therefore must be
    /// <c>partial</c>). The framework's own base classes implement those contracts but are hand-written and need no
    /// generation, so they are excluded. Returns the identifier location, the type name, and the contract's name.
    /// </summary>
    private static (Location Location, string Name, string Kind)? PartialViolation(GeneratorSyntaxContext ctx)
    {
        var decl = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(decl) is not INamedTypeSymbol type) return null;

        switch (type.ToDisplayString())
        {
            case "SimulaSharp.Kernel.Model.ModelBase":
            case "SimulaSharp.Kernel.Model.ServiceBase":
            case "SimulaSharp.Kernel.Model.LooselyCoupledServiceBase":
                return null;
        }

        bool isService = type.AllInterfaces.Any(static i => i.ToDisplayString() == IServiceFullName);
        bool isModel = type.AllInterfaces.Any(static i => i.ToDisplayString() == IModelFullName);
        if (!isModel && !isService) return null;

        return (decl.Identifier.GetLocation(), type.Name, isService ? "IService" : "IModel");
    }

    /// <summary>Binds one parameter from the assembly's Config dict; falls back to the attribute default, or throws if Required.</summary>
    private static void EmitApply(StringBuilder sb, string typeName, Param p)
    {
        sb.AppendLine("        {");
        sb.Append("            if (config.TryGetValue(\"").Append(p.Key).AppendLine("\", out var __value) && __value is not null)");
        sb.Append("                ").Append(p.Member.Name).Append(" = ").Append(p.Convert).AppendLine(";");
        if (p.Required)
        {
            sb.AppendLine("            else");
            sb.Append("                throw new global::System.InvalidOperationException(\"Missing required parameter '")
              .Append(p.Key).Append("' for ").Append(typeName).AppendLine(".\");");
        }
        else if (p.Default is { } d)
        {
            sb.AppendLine("            else");
            sb.Append("                ").Append(p.Member.Name).Append(" = (").Append(p.Type.ToDisplayString(Fq)).Append(")(")
              .Append(d.ToCSharpString()).AppendLine(");");
        }
        sb.AppendLine("        }");
    }

    /// <summary>Builds the ParameterInfo binder (boxed read/write over the member, plus the boxed default).</summary>
    private static void EmitCollect(StringBuilder sb, Param p)
    {
        string t = p.Type.ToDisplayString(Fq);
        sb.Append("        into.Add(new ").Append(P).AppendLine(".ParameterInfo");
        sb.AppendLine("        {");
        sb.Append("            Path = prefix + \".").Append(p.Member.Name).AppendLine("\",");
        sb.Append("            Name = ").Append(Str(p.Member.Name)).AppendLine(",");
        sb.Append("            ValueType = ").Append(Str(p.Type.ToDisplayString())).AppendLine(",");
        sb.Append("            ReadBoxed = () => ").Append(p.Member.Name).AppendLine(",");
        sb.Append("            WriteBoxed = __v => ").Append(p.Member.Name).Append(" = (").Append(t).AppendLine(")__v!,");
        if (p.Default is { } d)
            sb.Append("            Default = (").Append(t).Append(")(").Append(d.ToCSharpString()).AppendLine("),");
        sb.AppendLine("        });");
    }

    /// <summary>True if the member can be assigned from within the class (with its type).</summary>
    private static bool Settable(ISymbol member, out ITypeSymbol type)
    {
        switch (member)
        {
            case IPropertySymbol { SetMethod: { IsInitOnly: false } } p:
                type = p.Type;
                return true;
            case IFieldSymbol { IsReadOnly: false, IsConst: false } f:
                type = f.Type;
                return true;
            default:
                type = null!;
                return false;
        }
    }

    /// <summary>A C# expression converting string variable <paramref name="v"/> to <paramref name="type"/>, or "" if unsupported.</summary>
    private static string ConvertExpr(ITypeSymbol type, string v)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            string t = type.ToDisplayString(Fq);
            return $"({t})global::System.Enum.Parse(typeof({t}), {v}, true)";
        }

        return type.SpecialType switch
        {
            SpecialType.System_String => v,
            SpecialType.System_Boolean => $"global::System.Boolean.Parse({v})",
            SpecialType.System_Char => $"global::System.Char.Parse({v})",
            SpecialType.System_SByte => Num("global::System.SByte", v),
            SpecialType.System_Byte => Num("global::System.Byte", v),
            SpecialType.System_Int16 => Num("global::System.Int16", v),
            SpecialType.System_UInt16 => Num("global::System.UInt16", v),
            SpecialType.System_Int32 => Num("global::System.Int32", v),
            SpecialType.System_UInt32 => Num("global::System.UInt32", v),
            SpecialType.System_Int64 => Num("global::System.Int64", v),
            SpecialType.System_UInt64 => Num("global::System.UInt64", v),
            SpecialType.System_Single => Num("global::System.Single", v),
            SpecialType.System_Double => Num("global::System.Double", v),
            SpecialType.System_Decimal => Num("global::System.Decimal", v),
            _ => ""
        };
    }

    private static string Num(string t, string v) =>
        $"{t}.Parse({v}, global::System.Globalization.CultureInfo.InvariantCulture)";

    private static object? Named(AttributeData attr, string name) =>
        attr.NamedArguments.FirstOrDefault(n => n.Key == name).Value.Value;

    private static string Str(string? s) =>
        s is null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
