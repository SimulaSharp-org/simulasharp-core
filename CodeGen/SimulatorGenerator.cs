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
/// The generated <c>OnInitialize</c> creates the sites, constructs the services and the
/// model tree (depth-first, wiring containment and applying config), and binds service
/// dependencies. References are not yet wired (pending the reference-accessor generator).
/// <para>
/// Conventions (v1): YAML <c>Type</c> is the fully-qualified type name; models have a
/// <c>(IClock, IScheduler, ILogger)</c> ctor and services a <c>(IClock, ILogger)</c> ctor;
/// the generated class lives in the global namespace with a parameterless ctor.
/// </para>
/// </summary>
[Generator]
public sealed class SimulatorGenerator : IIncrementalGenerator
{
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

    /// <summary>An AdditionalFile is an assembly if it carries the <c>IsSimAssembly</c> metadata (the &lt;SimAssembly&gt; tag) or ends in <c>.simassembly.yaml</c>.</summary>
    private static bool IsSimAssembly(AdditionalText file, AnalyzerConfigOptionsProvider options)
    {
        if (options.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles.IsSimAssembly", out var v)
            && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return file.Path.EndsWith(".simassembly.yaml", StringComparison.OrdinalIgnoreCase);
    }

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

        var ctx = new EmitCtx();
        var init = new StringBuilder();

        // 1. Services
        foreach (var svc in sim.Services ?? [])
        {
            string v = ctx.NextVar();
            ctx.Services[svc.Name] = v;
            init.Append("        var ").Append(v).Append(" = RegisterService(new global::").Append(svc.Type)
                .Append("(Clock, CreateLogger(").Append(Str(name + "." + svc.Name)).AppendLine(")));");
            EmitConfig(init, v, svc.Config);
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

        // 4. Reference wiring: populate the reference collections, then build the accessors.
        foreach (var (modelVar, field, paths) in ctx.Refs)
        foreach (var p in paths)
            if (ctx.ModelsByPath.TryGetValue(p, out var targetVar))
                init.Append("        ").Append(modelVar).Append('.').Append(field).Append(".Add(").Append(targetVar).AppendLine(");");

        foreach (var modelVar in ctx.ModelVars)
            init.Append("        (").Append(modelVar).AppendLine(" as global::SimulaSharp.Kernel.Model.IReferenceWiring)?.WireReferences();");

        string missionStart = MissionStartExpr(sim.MissionStart);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.Append("public sealed class ").Append(name).AppendLine("Simulator : global::SimulaSharp.Kernel.Services.SimulatorBase");
        sb.AppendLine("{");
        sb.Append("    public ").Append(name).Append("Simulator() : base(").Append(missionStart).AppendLine(") { }");
        sb.AppendLine();
        sb.AppendLine("    protected override void OnInitialize()");
        sb.AppendLine("    {");
        sb.Append(init);
        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource($"{name}Simulator.g.cs", Text(sb.ToString()));
    }

    /// <summary>Emits construction + config + containment for a model node; returns its var. Records deps.</summary>
    private static string EmitModel(StringBuilder sb, ModelNode node, string siteVar, string path, EmitCtx ctx)
    {
        string fullPath = path + "." + node.Name;
        string v = ctx.NextVar();
        ctx.ModelsByPath[fullPath] = v;
        ctx.ModelVars.Add(v);

        sb.Append("        var ").Append(v).Append(" = Register(new global::").Append(node.Type)
            .Append("(Clock, ").Append(siteVar).Append(", CreateLogger(").Append(Str(fullPath)).AppendLine(")));");
        EmitConfig(sb, v, node.Config);
        sb.Append("        CollectKnobsFrom(").Append(v).Append(", ").Append(Str(fullPath)).AppendLine(");");
        sb.Append("        CollectPlottablesFrom(").Append(v).Append(", ").Append(Str(fullPath)).AppendLine(");");

        // Containment: each entry is { <containerFieldName>: [children] }
        foreach (var entry in node.Models ?? [])
        foreach (var kv in entry)
        foreach (var child in kv.Value ?? [])
        {
            string childVar = EmitModel(sb, child, siteVar, fullPath, ctx);
            sb.Append("        ").Append(v).Append('.').Append(kv.Key).Append(".Add(").Append(childVar).AppendLine(");");
        }

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
            .Append(".__ApplyConfig(new global::System.Collections.Generic.Dictionary<string, string?> { ");
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
    private static readonly DiagnosticDescriptor BadContainer = D("SIM007", "Invalid container", "Container field '{0}' was not found on '{1}' or is not a Container<T>");
    private static readonly DiagnosticDescriptor ContainerMismatch = D("SIM008", "Container type mismatch", "Contained type '{0}' is not assignable to container '{1}' element type '{2}'");
    private static readonly DiagnosticDescriptor BadReference = D("SIM009", "Invalid reference", "Reference field '{0}' was not found on '{1}' or is not a Reference<T>");
    private static readonly DiagnosticDescriptor UnresolvedRef = D("SIM010", "Unresolved reference", "Reference field '{0}' targets unknown instance '{1}'");
    private static readonly DiagnosticDescriptor RefMismatch = D("SIM011", "Reference type mismatch", "Referenced instance '{0}' is not assignable to reference '{1}' element type '{2}'");
    private static readonly DiagnosticDescriptor BadServiceRef = D("SIM012", "Invalid dependency", "Dependency member '{0}' was not found on '{1}' or is not [ServiceRef]");
    private static readonly DiagnosticDescriptor UnknownService = D("SIM013", "Unknown service", "Dependency '{0}' names unknown service '{1}'");
    private static readonly DiagnosticDescriptor ServiceMismatch = D("SIM014", "Service type mismatch", "Service '{0}' is not assignable to dependency member '{1}'");
    private static readonly DiagnosticDescriptor UnknownConfigKey = D("SIM015", "Unknown config key", "Config key '{0}' has no matching [Config] member on '{1}'", DiagnosticSeverity.Warning);

    // ── Validator ───────────────────────────────────────────────────────────────
    private sealed class Validator
    {
        private readonly Compilation _c;
        private readonly Location _loc;
        public readonly List<Diagnostic> Diagnostics = new();
        public bool HasErrors { get; private set; }

        private readonly INamedTypeSymbol? _iModel, _iService, _container, _reference, _configAttr, _serviceRefAttr, _iClock, _iScheduler, _iLogger;

        public Validator(Compilation c, Location loc)
        {
            _c = c;
            _loc = loc;
            _iModel = c.GetTypeByMetadataName("SimulaSharp.Kernel.Model.IModel");
            _iService = c.GetTypeByMetadataName("SimulaSharp.Kernel.Model.IService");
            _container = c.GetTypeByMetadataName("SimulaSharp.Kernel.Primitives.Container`1");
            _reference = c.GetTypeByMetadataName("SimulaSharp.Kernel.Primitives.Reference`1");
            _configAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ConfigAttribute");
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

            var paths = new Dictionary<string, INamedTypeSymbol>();
            var pendingRefs = new List<(string Field, ITypeSymbol? Elem, string Path)>();
            foreach (var site in sim.Sites ?? [])
                foreach (var root in site.Models ?? [])
                    WalkModel(root, simName, services, paths, pendingRefs);

            foreach (var (field, elem, target) in pendingRefs)
            {
                if (!paths.TryGetValue(target, out var t)) { Report(UnresolvedRef, field, target); continue; }
                if (elem is not null && !Assignable(t, elem)) Report(RefMismatch, target, field, elem.ToDisplayString());
            }
        }

        private void WalkModel(ModelNode node, string prefix, Dictionary<string, INamedTypeSymbol> services,
            Dictionary<string, INamedTypeSymbol> paths, List<(string, ITypeSymbol?, string)> pendingRefs)
        {
            string full = prefix + "." + node.Name;
            if (paths.ContainsKey(full)) Report(DuplicatePath, full);

            var type = ResolveType(node.Type, node.Name, service: false);
            if (type is null) return;
            paths[full] = type;
            CheckConfig(type, node.Config);

            foreach (var entry in node.Deps ?? [])
                foreach (var kv in entry)
                {
                    var member = FindMember(type, kv.Key);
                    if (member is null || !HasAttr(member, _serviceRefAttr)) Report(BadServiceRef, kv.Key, type.Name);
                    else if (!services.TryGetValue(kv.Value, out var svcType)) Report(UnknownService, kv.Key, kv.Value);
                    else if (MemberType(member) is { } mt && !Assignable(svcType, mt)) Report(ServiceMismatch, kv.Value, kv.Key);
                }

            foreach (var entry in node.Models ?? [])
                foreach (var kv in entry)
                {
                    var elem = GenericElement(MemberType(FindMember(type, kv.Key)), _container);
                    if (elem is null) Report(BadContainer, kv.Key, type.Name);
                    foreach (var child in kv.Value ?? [])
                    {
                        var childType = ResolveType(child.Type, child.Name, service: false);
                        if (elem is not null && childType is not null && !Assignable(childType, elem))
                            Report(ContainerMismatch, childType.ToDisplayString(), kv.Key, elem.ToDisplayString());
                        WalkModel(child, full, services, paths, pendingRefs);
                    }
                }

            foreach (var entry in node.Refs ?? [])
                foreach (var kv in entry)
                {
                    var elem = GenericElement(MemberType(FindMember(type, kv.Key)), _reference);
                    if (elem is null) Report(BadReference, kv.Key, type.Name);
                    foreach (var p in kv.Value ?? [])
                        pendingRefs.Add((kv.Key, elem, p));
                }
        }

        private INamedTypeSymbol? ResolveType(string typeName, string instance, bool service)
        {
            var t = _c.GetTypeByMetadataName(typeName);
            if (t is null) { Report(UnknownType, typeName, instance); return null; }
            var iface = service ? _iService : _iModel;
            if (iface is not null && !t.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iface)))
                Report(WrongKind, typeName, instance, service ? "IService" : "IModel");
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

        private static ITypeSymbol? GenericElement(ITypeSymbol? t, INamedTypeSymbol? def)
            => t is INamedTypeSymbol n && def is not null && SymbolEqualityComparer.Default.Equals(n.OriginalDefinition, def)
                ? n.TypeArguments[0] : null;

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
        public readonly List<string> ModelVars = new();
        public readonly List<(string ModelVar, string Member, string ServiceName)> Deps = new();
        public readonly List<(string ModelVar, string Field, List<string> Paths)> Refs = new();
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
    }

    private sealed class SiteDesc
    {
        public string Name { get; set; } = "";
        public int MaxEvents { get; set; } = 1024;
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
