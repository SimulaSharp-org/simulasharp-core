using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.Serialization;

namespace SimulaSharp.CodeGen;

/// <summary>
/// Generates a <c>&lt;Name&gt;Simulator : SimulatorBase</c> from each
/// <c>*.simassembly.yaml</c> (an <c>AdditionalFiles</c> item), parsed with YamlDotNet.
/// The generated <b>constructor</b> creates the sites, constructs the services and the
/// model tree (depth-first, wiring containment and applying parameters), binds service
/// dependencies, discovers knobs/plottables/parameters, and calls <c>OnAssembled</c> — so a
/// freshly-constructed simulator is already assembled and ready to tune then Configure.
/// <para>
/// Conventions (v1): YAML <c>Type</c> is the fully-qualified type name; models have a
/// <c>(IClock, IScheduler, ILogger)</c> ctor and services a <c>(IClock, ILogger)</c> ctor;
/// the generated class lives in the global namespace with a parameterless ctor.
/// </para>
/// </summary>
[Generator]
public sealed class SimulatorGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var assemblies = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(static p => IsSimAssembly(p.Left, p.Right))
            .Select(static (p, ct) => (Yaml: p.Left.GetText(ct)?.ToString(), p.Left.Path))
            .Where(static x => !string.IsNullOrWhiteSpace(x.Yaml));

        context.RegisterSourceOutput(assemblies.Combine(context.CompilationProvider),
            static (spc, pair) => Emit(spc, pair.Left.Yaml!, pair.Left.Path, pair.Right));
    }

    /// <summary>
    /// An AdditionalFile is an assembly when it carries the <c>IsSimAssembly</c> metadata — which the
    /// CodeGen targets set on every <c>&lt;SimAssembly&gt;</c> item. That tag is the single way to
    /// register an assembly; the file may be named anything (the <c>.simassembly.yaml</c> suffix is
    /// only a human convention).
    /// </summary>
    private static bool IsSimAssembly(AdditionalText file, AnalyzerConfigOptionsProvider options)
        => options.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles.IsSimAssembly", out var v)
           && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static void Emit(SourceProductionContext spc, string yaml, string path, Compilation compilation)
    {
        var loc = Location.Create(path, new TextSpan(0, 0),
            new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0)));

        SimAssembly? doc;
        try
        {
            doc = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<SimAssembly>(yaml);
        }
        catch (Exception ex)
        {
            spc.ReportDiagnostic(Diagnostic.Create(ParseError, loc, ex.Message));
            return;
        }

        var sim = doc?.Simulator;
        if (sim?.Name is not { Length: > 0 } name)
        {
            spc.ReportDiagnostic(Diagnostic.Create(MissingName, loc));
            return;
        }

        // Strict validation against the compilation's symbols.
        var validator = new Validator(compilation, loc);
        validator.Validate(sim, name);
        foreach (var d in validator.Diagnostics) spc.ReportDiagnostic(d);
        if (validator.HasErrors) return;   // don't emit on error — avoids cascading compile errors

        var ctx = new EmitCtx
        {
            Compilation = compilation,
            ContainsAttr = compilation.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ContainsAttribute`1"),
        };
        var init = new StringBuilder();

        // 1. Services. A clock-ungated (loosely-coupled) service registers through a different
        //    entry point — routed here by walking the concrete type's base chain.
        var looselyCoupledBase = compilation.GetTypeByMetadataName("SimulaSharp.Kernel.Model.LooselyCoupledServiceBase");
        foreach (var svc in sim.Services ?? [])
        {
            string v = ctx.NextVar();
            ctx.Services[svc.Name] = v;
            string register = DerivesFrom(compilation.GetTypeByMetadataName(svc.Type), looselyCoupledBase)
                ? "RegisterLooselyCoupledService" : "RegisterService";
            init.Append("        var ").Append(v).Append(" = ").Append(register).Append("(new global::").Append(svc.Type)
                .Append("(Clock, CreateLogger(").Append(Str(name + "." + svc.Name)).AppendLine(")));");
            EmitConfig(init, v, svc.Config);
            init.Append("        CollectParametersFrom(").Append(v).Append(", ").Append(Str(name + "." + svc.Name)).AppendLine(");");

            // Service→service deps: each entry is { <member>: serviceName }. Bound in the later pass
            // (loop 3) once every service var exists, so a service may reference one declared after it —
            // and two services may reference each other cyclically (setter injection breaks the cycle).
            foreach (var entry in svc.Deps ?? [])
            foreach (var kv in entry)
                ctx.Deps.Add((v, kv.Key, kv.Value));
        }

        // 2. Sites + model trees
        foreach (var site in sim.Sites ?? [])
        {
            string siteVar = ctx.NextVar();
            init.Append("        var ").Append(siteVar).Append(" = CreateSite(").Append(Str(site.Name))
                .Append(", ").Append(site.MaxEvents).AppendLine(");");
            foreach (var root in site.Models ?? [])
                EmitModel(init, root, siteVar, name, ctx);
        }

        // 3. Service dependency binding (after everything is constructed)
        foreach (var (modelVar, member, serviceName) in ctx.Deps)
        {
            if (ctx.Services.TryGetValue(serviceName, out var svcVar))
                init.Append("        ").Append(modelVar).Append('.').Append(member).Append(" = ").Append(svcVar).AppendLine(";");
        }

        // 4. Reference wiring: for each reference, build a RefCollection of pre-wrapped target Refs (the simulator
        //    constructs each `new <ConcreteType>Ref(target)` because only it knows the target's concrete type), then
        //    hand it to the referrer's hidden WireReference seam, which downcasts each Ref to the reference's type.
        foreach (var (modelVar, field, paths) in ctx.Refs)
        {
            string rc = ctx.NextVar();
            init.Append("        var ").Append(rc).AppendLine(" = new global::SimulaSharp.Kernel.Primitives.RefCollection();");
            foreach (var p in paths)
                if (ctx.ModelsByPath.TryGetValue(p, out var targetVar) && ctx.TypesByPath.TryGetValue(p, out var targetType))
                    init.Append("        ").Append(rc).Append(".Add(new global::").Append(targetType).Append("Ref(").Append(targetVar).AppendLine("));");
            init.Append("        ((global::SimulaSharp.Kernel.Model.IReferenceWiring)").Append(modelVar).Append(").WireReference(")
                .Append(Str(field)).Append(", ").Append(rc).AppendLine(");");
        }

        string missionStart = MissionStartExpr(sim.MissionStart);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.Append("public sealed class ").Append(name).AppendLine("Simulator : global::SimulaSharp.Kernel.Services.SimulatorBase");
        sb.AppendLine("{");
        // The topology is assembled in the constructor: a freshly-new'd simulator is ready to tune
        // (GetParams/SetParam) and Configure — there is no separate Initialize() phase.
        sb.Append("    public ").Append(name).Append("Simulator() : base(").Append(missionStart).AppendLine(")");
        sb.AppendLine("    {");
        sb.Append(init);
        sb.AppendLine("        OnAssembled();");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource($"{name}Simulator.g.cs", Text(sb.ToString()));
    }

    /// <summary>
    /// Emits construction + config + containment for a model node <b>bottom-up</b>; returns its var. Each
    /// container's children are constructed first, collected into a non-generic <c>ContainerFactory</c>, then
    /// frozen via <c>Build&lt;T&gt;()</c> and handed to the parent through its <c>init</c>-only accessor — so a
    /// child never needs the not-yet-constructed parent. Records service deps and references for later passes.
    /// </summary>
    private static string EmitModel(StringBuilder sb, ModelNode node, string siteVar, string path, EmitCtx ctx)
    {
        string fullPath = path + "." + node.Name;

        // Containment: each entry is { <containerName>: [children] }. Build every container before the parent.
        var inits = new List<string>();
        foreach (var entry in node.Models ?? [])
        foreach (var kv in entry)
        {
            string f = ctx.NextVar();
            sb.Append("        var ").Append(f).AppendLine(" = new global::SimulaSharp.Kernel.Primitives.ContainerFactory();");
            foreach (var child in kv.Value ?? [])
            {
                string childVar = EmitModel(sb, child, siteVar, fullPath, ctx);
                sb.Append("        ").Append(f).Append(".Add(").Append(childVar).AppendLine(");");
            }
            inits.Add(kv.Key + " = " + f + ".Build<" + ctx.ContainerElementType(node.Type, kv.Key) + ">()");
        }

        string v = ctx.NextVar();
        ctx.ModelsByPath[fullPath] = v;
        ctx.TypesByPath[fullPath] = node.Type;
        ctx.ModelVars.Add(v);

        sb.Append("        var ").Append(v).Append(" = Register(new global::").Append(node.Type)
            .Append("(Clock, ").Append(siteVar).Append(", CreateLogger(").Append(Str(fullPath)).Append("))");
        if (inits.Count > 0) sb.Append(" { ").Append(string.Join(", ", inits)).Append(" }");
        sb.AppendLine(");");
        EmitConfig(sb, v, node.Config);
        sb.Append("        CollectKnobsFrom(").Append(v).Append(", ").Append(Str(fullPath)).AppendLine(");");
        sb.Append("        CollectPlottablesFrom(").Append(v).Append(", ").Append(Str(fullPath)).AppendLine(");");
        sb.Append("        CollectParametersFrom(").Append(v).Append(", ").Append(Str(fullPath)).AppendLine(");");

        // Deps: each entry is { <member>: serviceName } — bound in a later pass.
        foreach (var entry in node.Deps ?? [])
        foreach (var kv in entry)
            ctx.Deps.Add((v, kv.Key, kv.Value));

        // References: each entry is { <referenceFieldName>: [full.path, …] } — wired in a later pass.
        foreach (var entry in node.Refs ?? [])
        foreach (var kv in entry)
            ctx.Refs.Add((v, kv.Key, kv.Value ?? []));

        return v;
    }

    private static void EmitConfig(StringBuilder sb, string target, Dictionary<string, string>? config)
    {
        if (config is null || config.Count == 0) return;
        sb.Append("        ").Append(target)
            .Append(".__ApplyParameters(new global::System.Collections.Generic.Dictionary<string, string?> { ");
        sb.Append(string.Join(", ", config.Select(kv => $"[{Str(kv.Key)}] = {Str(kv.Value)}")));
        sb.AppendLine(" });");
    }

    private static string MissionStartExpr(string? missionStart)
    {
        if (!string.IsNullOrWhiteSpace(missionStart)
            && DateTime.TryParse(missionStart, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return $"new global::System.DateTime({dt.Ticks}L, global::System.DateTimeKind.Utc)";
        return "global::System.DateTime.UnixEpoch";
    }

    /// <summary>True if <paramref name="type"/> (or an ancestor) is <paramref name="baseType"/>. False if either is null.</summary>
    private static bool DerivesFrom(INamedTypeSymbol? type, INamedTypeSymbol? baseType)
    {
        if (baseType is null) return false;
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, baseType)) return true;
        return false;
    }

    private static string Str(string? s) =>
        s is null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static SourceText Text(string s) => SourceText.From(s + "\n", Encoding.UTF8);

    // ── Diagnostics ─────────────────────────────────────────────────────────────
    private const string Cat = "SimulaSharp.Assembly";
    private static DiagnosticDescriptor D(string id, string title, string fmt, DiagnosticSeverity sev = DiagnosticSeverity.Error)
        => new(id, title, fmt, Cat, sev, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ParseError = D("SIM001", "Assembly parse error", "Assembly YAML parse error: {0}");
    private static readonly DiagnosticDescriptor MissingName = D("SIM002", "Missing simulator name", "Assembly is missing 'Simulator.Name'");
    private static readonly DiagnosticDescriptor DuplicatePath = D("SIM003", "Duplicate instance", "Duplicate instance path '{0}'");
    private static readonly DiagnosticDescriptor UnknownType = D("SIM004", "Unknown type", "Type '{0}' for instance '{1}' could not be found");
    private static readonly DiagnosticDescriptor WrongKind = D("SIM005", "Wrong component kind", "Type '{0}' for '{1}' does not implement {2}");
    private static readonly DiagnosticDescriptor MissingCtor = D("SIM006", "Missing constructor", "Type '{0}' lacks the required constructor {1}");
    private static readonly DiagnosticDescriptor NoContainer = D("SIM007", "Unknown container", "The model '{0}' does not have a SubModel container named '{1}'");
    private static readonly DiagnosticDescriptor ContainerMismatch = D("SIM008", "Container type mismatch", "The Model '{0}' does not implement/inherit-from '{1}'");
    private static readonly DiagnosticDescriptor NotAModel = D("SIM009", "Not a model", "'{0}' is not a model (does not implement IModel)");
    private static readonly DiagnosticDescriptor BadReference = D("SIM010", "Unknown reference", "The model '{1}' does not have a reference named '{0}' (declare it with [References<T>])");
    private static readonly DiagnosticDescriptor UnresolvedRef = D("SIM011", "Unresolved reference", "Reference field '{0}' targets unknown instance '{1}'");
    private static readonly DiagnosticDescriptor RefMismatch = D("SIM012", "Reference type mismatch", "Referenced instance '{0}' is not assignable to reference '{1}' element type '{2}'");
    private static readonly DiagnosticDescriptor BadServiceRef = D("SIM013", "Invalid dependency", "Dependency member '{0}' was not found on '{1}' or is not [ServiceRef]");
    private static readonly DiagnosticDescriptor UnknownService = D("SIM014", "Unknown service", "Dependency '{0}' names unknown service '{1}'");
    private static readonly DiagnosticDescriptor ServiceMismatch = D("SIM015", "Service type mismatch", "Service '{0}' is not assignable to dependency member '{1}'");
    private static readonly DiagnosticDescriptor UnknownConfigKey = D("SIM016", "Unknown config key", "Config key '{0}' has no matching [Parameter] member on '{1}'", DiagnosticSeverity.Warning);

    // ── Validator ───────────────────────────────────────────────────────────────
    private sealed class Validator
    {
        private readonly Compilation _c;
        private readonly Location _loc;
        public readonly List<Diagnostic> Diagnostics = new();
        public bool HasErrors { get; private set; }

        private readonly INamedTypeSymbol? _iModel, _iService, _containsAttr, _referencesAttr, _configAttr, _serviceRefAttr, _iClock, _iScheduler, _iLogger;

        public Validator(Compilation c, Location loc)
        {
            _c = c;
            _loc = loc;
            _iModel = c.GetTypeByMetadataName("SimulaSharp.Kernel.Model.IModel");
            _iService = c.GetTypeByMetadataName("SimulaSharp.Kernel.Model.IService");
            _containsAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ContainsAttribute`1");
            _referencesAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ReferencesAttribute`1");
            _configAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ParameterAttribute");
            _serviceRefAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ServiceRefAttribute");
            _iClock = c.GetTypeByMetadataName("SimulaSharp.Kernel.Services.IClock");
            _iScheduler = c.GetTypeByMetadataName("SimulaSharp.Kernel.Services.IScheduler");
            _iLogger = c.GetTypeByMetadataName("SimulaSharp.Kernel.Logging.ILogger");
        }

        private void Report(DiagnosticDescriptor d, params object[] args)
        {
            var diag = Diagnostic.Create(d, _loc, args);
            if (diag.Severity == DiagnosticSeverity.Error) HasErrors = true;
            Diagnostics.Add(diag);
        }

        public void Validate(SimulatorDesc sim, string simName)
        {
            var services = new Dictionary<string, INamedTypeSymbol>();
            foreach (var svc in sim.Services ?? [])
            {
                var t = ResolveType(svc.Type, svc.Name, service: true);
                CheckConfig(t, svc.Config);
                if (t is not null && !services.ContainsKey(svc.Name)) services[svc.Name] = t;
            }

            // Service→service deps, validated after the whole service table is known so a service may
            // reference one declared later (and two services may reference each other cyclically).
            foreach (var svc in sim.Services ?? [])
                if (services.TryGetValue(svc.Name, out var holder))
                    foreach (var entry in svc.Deps ?? [])
                        foreach (var kv in entry)
                            CheckDep(holder, kv.Key, kv.Value, services);

            var paths = new Dictionary<string, INamedTypeSymbol>();
            var pendingRefs = new List<(string Field, ITypeSymbol? Elem, string Path)>();
            foreach (var site in sim.Sites ?? [])
                foreach (var root in site.Models ?? [])
                    WalkModel(root, simName, services, paths, pendingRefs, containedPath: null);

            foreach (var (field, elem, target) in pendingRefs)
            {
                if (!paths.TryGetValue(target, out var t)) { Report(UnresolvedRef, field, target); continue; }
                if (elem is not null && !Assignable(t, elem)) Report(RefMismatch, target, field, elem.ToDisplayString());
            }
        }

        private INamedTypeSymbol? WalkModel(ModelNode node, string prefix, Dictionary<string, INamedTypeSymbol> services,
            Dictionary<string, INamedTypeSymbol> paths, List<(string, ITypeSymbol?, string)> pendingRefs, string? containedPath)
        {
            string full = prefix + "." + node.Name;
            if (paths.ContainsKey(full)) Report(DuplicatePath, full);

            var type = ResolveType(node.Type, node.Name, service: false, containedPath);
            if (type is null) return null;
            paths[full] = type;
            CheckConfig(type, node.Config);

            foreach (var entry in node.Deps ?? [])
                foreach (var kv in entry)
                    CheckDep(type, kv.Key, kv.Value, services);

            // Containment: each entry is { <containerName>: [children] }. The container is declared by a
            // [Contains<T>("containerName")] attribute on the model (not a field); its element type is T.
            foreach (var entry in node.Models ?? [])
                foreach (var kv in entry)
                {
                    var elem = ContainerElement(type, kv.Key);
                    if (elem is null) Report(NoContainer, type.Name, kv.Key);
                    foreach (var child in kv.Value ?? [])
                    {
                        string childFull = full + "." + child.Name;
                        var childType = WalkModel(child, full, services, paths, pendingRefs, childFull);
                        // Assignability (SIM008) only matters once we know the child IS a model — a non-model
                        // child already reported SIM009 in its own WalkModel, so don't pile a second error on.
                        if (elem is not null && childType is not null
                            && childType.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _iModel))
                            && !Assignable(childType, elem))
                            Report(ContainerMismatch, childFull, elem.ToDisplayString());
                    }
                }

            // References: each entry is { <referenceName>: [dotted.paths] }. The reference is declared by a
            // [References<T>("referenceName")] attribute on the model (not a field); its target type is T.
            foreach (var entry in node.Refs ?? [])
                foreach (var kv in entry)
                {
                    var elem = ReferenceTarget(type, kv.Key);
                    if (elem is null) Report(BadReference, kv.Key, type.Name);
                    foreach (var p in kv.Value ?? [])
                        pendingRefs.Add((kv.Key, elem, p));
                }

            return type;
        }

        /// <summary>The target type <c>T</c> of the <c>[References&lt;T&gt;("name")]</c> reference declared on
        /// <paramref name="type"/> (walking base types), or null when the model declares no such reference.</summary>
        private ITypeSymbol? ReferenceTarget(INamedTypeSymbol type, string name)
        {
            for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
                foreach (var a in t.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(a.AttributeClass?.OriginalDefinition, _referencesAttr)
                        && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is string n && n == name
                        && a.AttributeClass is { TypeArguments.Length: 1 } ac)
                        return ac.TypeArguments[0];
            return null;
        }

        /// <summary>The element type <c>T</c> of the <c>[Contains&lt;T&gt;("name")]</c> container declared on
        /// <paramref name="type"/> (walking base types), or null when the model declares no such container.</summary>
        private ITypeSymbol? ContainerElement(INamedTypeSymbol type, string name)
        {
            for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
                foreach (var a in t.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(a.AttributeClass?.OriginalDefinition, _containsAttr)
                        && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is string n && n == name
                        && a.AttributeClass is { TypeArguments.Length: 1 } ac)
                        return ac.TypeArguments[0];
            return null;
        }

        /// <summary>
        /// Validates one <c>[ServiceRef]</c> dependency on <paramref name="holderType"/> (a model or a
        /// service): the member exists and is <c>[ServiceRef]</c>, the named service is known, and it is
        /// assignable to the member. Shared by model deps and service→service deps.
        /// </summary>
        private void CheckDep(INamedTypeSymbol holderType, string member, string targetService,
            Dictionary<string, INamedTypeSymbol> services)
        {
            var m = FindMember(holderType, member);
            if (m is null || !HasAttr(m, _serviceRefAttr)) Report(BadServiceRef, member, holderType.Name);
            else if (!services.TryGetValue(targetService, out var svcType)) Report(UnknownService, member, targetService);
            else if (MemberType(m) is { } mt && !Assignable(svcType, mt)) Report(ServiceMismatch, targetService, member);
        }

        private INamedTypeSymbol? ResolveType(string typeName, string instance, bool service, string? containedPath = null)
        {
            var t = _c.GetTypeByMetadataName(typeName);
            if (t is null) { Report(UnknownType, typeName, instance); return null; }
            var iface = service ? _iService : _iModel;
            if (iface is not null && !t.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iface)))
            {
                // A contained child that isn't a model gets the containment-specific SIM009 (with its full
                // path); everything else — root models and services — gets the general SIM005.
                if (containedPath is not null) Report(NotAModel, containedPath);
                else Report(WrongKind, typeName, instance, service ? "IService" : "IModel");
            }
            if (!HasCtor(t, service))
                Report(MissingCtor, typeName, service ? "(IClock, ILogger)" : "(IClock, IScheduler, ILogger)");
            return t;
        }

        private bool HasCtor(INamedTypeSymbol t, bool service)
        {
            var want = service ? new[] { _iClock, _iLogger } : new[] { _iClock, _iScheduler, _iLogger };
            if (want.Any(static w => w is null)) return true;   // can't verify → don't false-positive
            return t.InstanceConstructors.Any(ctor => ctor.DeclaredAccessibility == Accessibility.Public
                && ctor.Parameters.Length == want.Length
                && ctor.Parameters.Select((p, i) => SymbolEqualityComparer.Default.Equals(p.Type, want[i])).All(static x => x));
        }

        private static ISymbol? FindMember(INamedTypeSymbol type, string name)
        {
            for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
            {
                var m = t.GetMembers(name).FirstOrDefault(static x => x is IPropertySymbol or IFieldSymbol);
                if (m is not null) return m;
            }
            return null;
        }

        private static ITypeSymbol? MemberType(ISymbol? m) => m switch
        {
            IPropertySymbol p => p.Type,
            IFieldSymbol f => f.Type,
            _ => null
        };

        private bool Assignable(ITypeSymbol source, ITypeSymbol dest)
        {
            var conv = _c.ClassifyCommonConversion(source, dest);
            return conv.IsIdentity || conv.IsImplicit;
        }

        private static bool HasAttr(ISymbol m, INamedTypeSymbol? attr)
            => attr is not null && m.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attr));

        private void CheckConfig(INamedTypeSymbol? type, Dictionary<string, string>? config)
        {
            if (type is null || config is null) return;
            foreach (var key in config.Keys)
            {
                bool found = false;
                for (INamedTypeSymbol? t = type; t is not null && !found; t = t.BaseType)
                    foreach (var m in t.GetMembers())
                    {
                        var a = m.GetAttributes().FirstOrDefault(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, _configAttr));
                        if (a is null) continue;
                        string ck = a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is string k ? k : m.Name;
                        if (ck == key) { found = true; break; }
                    }
                if (!found) Report(UnknownConfigKey, key, type.Name);
            }
        }
    }

    // ── Emission state ──────────────────────────────────────────────────────────
    private sealed class EmitCtx
    {
        private int _n;
        public string NextVar() => "__c" + _n++;
        public readonly Dictionary<string, string> Services = new();
        public readonly Dictionary<string, string> ModelsByPath = new();
        public readonly Dictionary<string, string> TypesByPath = new();
        public readonly List<string> ModelVars = new();
        public readonly List<(string ModelVar, string Member, string ServiceName)> Deps = new();
        public readonly List<(string ModelVar, string Field, List<string> Paths)> Refs = new();

        public Compilation Compilation = null!;
        public INamedTypeSymbol? ContainsAttr;

        /// <summary>The fully-qualified element type <c>T</c> of the <c>[Contains&lt;T&gt;("name")]</c> container
        /// on <paramref name="parentType"/>, for emitting <c>Build&lt;T&gt;()</c>. Validation has already
        /// guaranteed the attribute exists; falls back to <c>IModel</c> defensively.</summary>
        public string ContainerElementType(string parentType, string name)
        {
            for (INamedTypeSymbol? t = Compilation.GetTypeByMetadataName(parentType); t is not null; t = t.BaseType)
                foreach (var a in t.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(a.AttributeClass?.OriginalDefinition, ContainsAttr)
                        && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is string n && n == name
                        && a.AttributeClass is { TypeArguments.Length: 1 } ac)
                        return ac.TypeArguments[0].ToDisplayString(Fq);
            return "global::SimulaSharp.Kernel.Model.IModel";
        }
    }

    // ── Assembly descriptor (YamlDotNet target) ─────────────────────────────────
    private sealed class SimAssembly { public SimulatorDesc? Simulator { get; set; } }

    private sealed class SimulatorDesc
    {
        public string? Name { get; set; }
        public string? MissionStart { get; set; }
        public List<ServiceDesc>? Services { get; set; }
        public List<SiteDesc>? Sites { get; set; }
    }

    private sealed class ServiceDesc
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public Dictionary<string, string>? Config { get; set; }
        public List<Dictionary<string, string>>? Deps { get; set; }              // service→service deps
    }

    private sealed class SiteDesc
    {
        public string Name { get; set; } = "";
        public int MaxEvents { get; set; } = 64;
        public List<ModelNode>? Models { get; set; }
    }

    private sealed class ModelNode
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public Dictionary<string, string>? Config { get; set; }
        public List<Dictionary<string, List<ModelNode>>>? Models { get; set; }   // containment
        public List<Dictionary<string, List<string>>>? Refs { get; set; }        // references (deferred)
        public List<Dictionary<string, string>>? Deps { get; set; }              // service deps
    }
}
