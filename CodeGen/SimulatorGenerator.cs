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
            CrossRefAttr = compilation.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.CrossReferenceAttribute`1"),
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

        // 1b. Global events — scheduler-less pub/sub channels. Instantiated up front (they depend on nothing) so the
        //     later pass can register subscribers and fill producer handles; rooted via RegisterEvent for the run.
        foreach (var evt in sim.Events ?? [])
        {
            string v = ctx.NextVar();
            ctx.Events[evt.Name] = v;
            init.Append("        var ").Append(v).Append(" = RegisterEvent(").Append(EventCtor(evt)).AppendLine(");");
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

        // 3b. Event wiring (after everything is constructed): register each [EventHandler] subscriber on its event
        //     (the handler runs on the model's own site), and fill each [EventReference] producer handle with the
        //     shared event. Registration is synchronous here — before the run — so the stock event needs no locking.
        foreach (var (modelVar, eventName, handler) in ctx.Subs)
            if (ctx.Events.TryGetValue(eventName, out var evtVar))
                init.Append("        ").Append(evtVar).Append(".Register(").Append(modelVar).Append(", ").Append(modelVar).Append('.').Append(handler).AppendLine(");");
        foreach (var (modelVar, eventName, member) in ctx.Trigs)
            if (ctx.Events.TryGetValue(eventName, out var evtVar))
                init.Append("        ").Append(modelVar).Append('.').Append(member).Append(" = ").Append(evtVar).AppendLine(";");

        // 4. Reference wiring (after everything is constructed). A cross-site [CrossReference<T>] is wired through a
        //    RefCollection of pre-wrapped target Refs (the simulator constructs each `new <ConcreteType>Ref(target)`
        //    because only it knows the target's concrete type), handed to the referrer's hidden WireReference seam. An
        //    intra-site [Reference<T>] needs no Ref: the target instances themselves (same site) are handed to the
        //    WireLocalReference seam, which casts each to the reference's element type.
        foreach (var (modelVar, callerSite, field, elemIface, paths, isCross) in ctx.Refs)
        {
            if (isCross)
            {
                // Each target is wrapped as `new <I>Ref((I)target, targetSite, callerSite)` — the Ref captures both
                // schedulers so it can dispatch (same-site inline / cross-site marshal) without a per-call scheduler.
                string rc = ctx.NextVar();
                init.Append("        var ").Append(rc).AppendLine(" = new global::SimulaSharp.Kernel.Primitives.RefCollection();");
                foreach (var p in paths)
                    if (ctx.ModelsByPath.TryGetValue(p, out var targetVar) && ctx.SiteVarByPath.TryGetValue(p, out var targetSite))
                        init.Append("        ").Append(rc).Append(".Add(new ").Append(elemIface).Append("Ref((")
                            .Append(elemIface).Append(')').Append(targetVar).Append(", ").Append(targetSite).Append(", ").Append(callerSite).AppendLine("));");
                init.Append("        ((global::SimulaSharp.Kernel.Model.IReferenceWiring)").Append(modelVar).Append(").WireReference(")
                    .Append(Str(field)).Append(", ").Append(rc).AppendLine(");");
            }
            else
            {
                var targets = paths.Where(p => ctx.ModelsByPath.ContainsKey(p)).Select(p => ctx.ModelsByPath[p]);
                init.Append("        ((global::SimulaSharp.Kernel.Model.ILocalReferenceWiring)").Append(modelVar).Append(").WireLocalReference(")
                    .Append(Str(field)).Append(", new global::System.Collections.Generic.List<global::SimulaSharp.Kernel.Model.IModel> { ")
                    .Append(string.Join(", ", targets)).AppendLine(" });");
            }
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
        ctx.SiteVarByPath[fullPath] = siteVar;
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

        // References, wired in a later pass. `Refs:` are intra-site (direct) and `CRefs:` are cross-site (through a
        // Ref); each entry is { <referenceFieldName>: [full.path, …] }. For cross refs the element interface is
        // resolved and the referrer's own site var captured, so the Ref can be built (target, targetSched, callerSched).
        foreach (var entry in node.Refs ?? [])
        foreach (var kv in entry)
            ctx.Refs.Add((v, siteVar, kv.Key, "", kv.Value ?? [], false));
        foreach (var entry in node.CRefs ?? [])
        foreach (var kv in entry)
            ctx.Refs.Add((v, siteVar, kv.Key, ctx.CrossReferenceElementType(node.Type, kv.Key), kv.Value ?? [], true));

        // Event subscriptions / triggers, wired in the later pass. Each entry is { <member>: eventName }:
        // Subscriptions bind an [EventHandler] method, Triggers an [EventReference] producer member.
        foreach (var entry in node.Subscriptions ?? [])
        foreach (var kv in entry)
            ctx.Subs.Add((v, kv.Value, kv.Key));
        foreach (var entry in node.Triggers ?? [])
        foreach (var kv in entry)
            ctx.Trigs.Add((v, kv.Value, kv.Key));

        return v;
    }

    /// <summary>The constructor expression for a declared event: a custom <c>Type</c> if given, else the stock
    /// <c>SimEvent&lt;ArgType&gt;</c> (payload) or <c>SimEvent</c> (argless).</summary>
    private static string EventCtor(EventDesc evt) =>
        !string.IsNullOrWhiteSpace(evt.Type) ? "new global::" + evt.Type + "()"
        : !string.IsNullOrWhiteSpace(evt.ArgType) ? "new global::SimulaSharp.Kernel.Events.SimEvent<global::" + evt.ArgType + ">()"
        : "new global::SimulaSharp.Kernel.Events.SimEvent()";

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
    private static readonly DiagnosticDescriptor BadReference = D("SIM010", "Unknown reference", "The model '{1}' does not have a reference named '{0}' (declare it with [Reference<T>] for 'Refs:' or [CrossReference<T>] for 'CRefs:')");
    private static readonly DiagnosticDescriptor UnresolvedRef = D("SIM011", "Unresolved reference", "Reference field '{0}' targets unknown instance '{1}'");
    private static readonly DiagnosticDescriptor RefMismatch = D("SIM012", "Reference type mismatch", "Referenced instance '{0}' is not assignable to reference '{1}' element type '{2}'");
    private static readonly DiagnosticDescriptor BadServiceRef = D("SIM013", "Invalid dependency", "Dependency member '{0}' was not found on '{1}' or is not [ServiceRef]");
    private static readonly DiagnosticDescriptor UnknownService = D("SIM014", "Unknown service", "Dependency '{0}' names unknown service '{1}'");
    private static readonly DiagnosticDescriptor ServiceMismatch = D("SIM015", "Service type mismatch", "Service '{0}' is not assignable to dependency member '{1}'");
    private static readonly DiagnosticDescriptor UnknownConfigKey = D("SIM016", "Unknown config key", "Config key '{0}' has no matching [Parameter] member on '{1}'", DiagnosticSeverity.Warning);
    private static readonly DiagnosticDescriptor LocalRefCrossesSite = D("SIM017", "Intra-site reference crosses a site", "Intra-site reference '{0}' targets '{1}' on a different site — declare it as [CrossReference<T>] to cross sites");
    private static readonly DiagnosticDescriptor CrossRefNotInterface = D("SIM018", "Cross-reference target is not an interface", "Cross-reference '{0}' on model '{1}' must target an interface (a concrete type is not allowed for [CrossReference<T>])");
    private static readonly DiagnosticDescriptor UnknownEvent = D("SIM023", "Unknown event", "'{0}' names unknown event '{1}' — declare it under the top-level 'Events:' block");
    private static readonly DiagnosticDescriptor BadEventHandler = D("SIM024", "Invalid event handler", "Event handler '{0}' was not found on '{1}', is not [EventHandler], or is not a void method");
    private static readonly DiagnosticDescriptor HandlerSignature = D("SIM025", "Handler signature mismatch", "Event handler '{0}' on '{1}' is incompatible with event '{2}' — expected {3}");
    private static readonly DiagnosticDescriptor SubscriberKind = D("SIM026", "Wrong subscriber type", "Model '{0}' cannot subscribe to event '{1}': its type '{2}' is not assignable to the event's subscriber type '{3}'");
    private static readonly DiagnosticDescriptor BadEventReference = D("SIM027", "Invalid event reference", "Trigger member '{0}' on '{1}' must be [EventReference] and typed as {2}");
    private static readonly DiagnosticDescriptor BadEventType = D("SIM028", "Invalid event type", "Event '{0}': type '{1}' must implement ISimEvent<…> and match the declared ArgType");

    // ── Validator ───────────────────────────────────────────────────────────────
    private sealed class Validator
    {
        private readonly Compilation _c;
        private readonly Location _loc;
        public readonly List<Diagnostic> Diagnostics = new();
        public bool HasErrors { get; private set; }

        private readonly INamedTypeSymbol? _iModel, _iService, _containsAttr, _referencesAttr, _localReferencesAttr, _configAttr, _serviceRefAttr, _iClock, _iScheduler, _iLogger;
        private readonly INamedTypeSymbol? _eventHandlerAttr, _eventRefAttr, _iSimEvent1, _iSimEvent2, _iInvocator, _iInvocator1;

        public Validator(Compilation c, Location loc)
        {
            _c = c;
            _loc = loc;
            _iModel = c.GetTypeByMetadataName("SimulaSharp.Kernel.Model.IModel");
            _iService = c.GetTypeByMetadataName("SimulaSharp.Kernel.Model.IService");
            _containsAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ContainsAttribute`1");
            _referencesAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.CrossReferenceAttribute`1");
            _localReferencesAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ReferenceAttribute`1");
            _configAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ParameterAttribute");
            _serviceRefAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.ServiceRefAttribute");
            _iClock = c.GetTypeByMetadataName("SimulaSharp.Kernel.Services.IClock");
            _iScheduler = c.GetTypeByMetadataName("SimulaSharp.Kernel.Services.IScheduler");
            _iLogger = c.GetTypeByMetadataName("SimulaSharp.Kernel.Logging.ILogger");
            _eventHandlerAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.EventHandlerAttribute");
            _eventRefAttr = c.GetTypeByMetadataName("SimulaSharp.Kernel.Attributes.EventReferenceAttribute");
            _iSimEvent1 = c.GetTypeByMetadataName("SimulaSharp.Kernel.Events.ISimEvent`1");
            _iSimEvent2 = c.GetTypeByMetadataName("SimulaSharp.Kernel.Events.ISimEvent`2");
            _iInvocator = c.GetTypeByMetadataName("SimulaSharp.Kernel.Events.ISimEventInvocator");
            _iInvocator1 = c.GetTypeByMetadataName("SimulaSharp.Kernel.Events.ISimEventInvocator`1");
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

            // Global events, resolved up front so Subscriptions/Triggers can be checked against each event's shape.
            var events = new Dictionary<string, EventInfo>();
            foreach (var evt in sim.Events ?? [])
                if (ResolveEvent(evt) is { } info && !events.ContainsKey(evt.Name)) events[evt.Name] = info;

            var paths = new Dictionary<string, INamedTypeSymbol>();
            var sitesByPath = new Dictionary<string, string>();
            var pendingRefs = new List<(string Field, ITypeSymbol? Elem, string Path, bool IsCross, string RefSite)>();
            foreach (var site in sim.Sites ?? [])
                foreach (var root in site.Models ?? [])
                    WalkModel(root, simName, site.Name, services, events, paths, sitesByPath, pendingRefs, containedPath: null);

            foreach (var (field, elem, target, isCross, refSite) in pendingRefs)
            {
                if (!paths.TryGetValue(target, out var t)) { Report(UnresolvedRef, field, target); continue; }
                if (elem is not null && !Assignable(t, elem)) Report(RefMismatch, target, field, elem.ToDisplayString());
                // An intra-site [Reference<T>] must resolve to a target on the same site as the referrer.
                if (!isCross && sitesByPath.TryGetValue(target, out var tSite) && tSite != refSite)
                    Report(LocalRefCrossesSite, field, target);
            }
        }

        private INamedTypeSymbol? WalkModel(ModelNode node, string prefix, string siteName, Dictionary<string, INamedTypeSymbol> services,
            Dictionary<string, EventInfo> events, Dictionary<string, INamedTypeSymbol> paths, Dictionary<string, string> sitesByPath,
            List<(string, ITypeSymbol?, string, bool, string)> pendingRefs, string? containedPath)
        {
            string full = prefix + "." + node.Name;
            if (paths.ContainsKey(full)) Report(DuplicatePath, full);

            var type = ResolveType(node.Type, node.Name, service: false, containedPath);
            if (type is null) return null;
            paths[full] = type;
            sitesByPath[full] = siteName;
            CheckConfig(type, node.Config);

            foreach (var entry in node.Deps ?? [])
                foreach (var kv in entry)
                    CheckDep(type, kv.Key, kv.Value, services);

            // Event wiring: Subscriptions bind an [EventHandler] method, Triggers an [EventReference] producer member.
            foreach (var entry in node.Subscriptions ?? [])
                foreach (var kv in entry)
                    CheckSubscription(type, node.Name, kv.Key, kv.Value, events);
            foreach (var entry in node.Triggers ?? [])
                foreach (var kv in entry)
                    CheckTrigger(type, kv.Key, kv.Value, events);

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
                        var childType = WalkModel(child, full, siteName, services, events, paths, sitesByPath, pendingRefs, childFull);
                        // Assignability (SIM008) only matters once we know the child IS a model — a non-model
                        // child already reported SIM009 in its own WalkModel, so don't pile a second error on.
                        if (elem is not null && childType is not null
                            && childType.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _iModel))
                            && !Assignable(childType, elem))
                            Report(ContainerMismatch, childFull, elem.ToDisplayString());
                    }
                }

            // References come in two YAML blocks. `Refs:` are intra-site [Reference<T>] (same-site, direct); `CRefs:`
            // are cross-site [CrossReference<T>] (interface-only, through a Ref). Each block validates against its own
            // attribute kind; the entry shape is { <referenceName>: [dotted.paths] } in both.
            foreach (var entry in node.Refs ?? [])
                foreach (var kv in entry)
                {
                    var elem = ReferenceTarget(type, kv.Key, _localReferencesAttr);
                    if (elem is null) { Report(BadReference, kv.Key, type.Name); continue; }
                    foreach (var p in kv.Value ?? [])
                        pendingRefs.Add((kv.Key, elem, p, false, siteName));
                }
            foreach (var entry in node.CRefs ?? [])
                foreach (var kv in entry)
                {
                    var elem = ReferenceTarget(type, kv.Key, _referencesAttr);
                    if (elem is null) { Report(BadReference, kv.Key, type.Name); continue; }
                    if (elem is { TypeKind: not TypeKind.Interface }) Report(CrossRefNotInterface, kv.Key, type.Name);
                    foreach (var p in kv.Value ?? [])
                        pendingRefs.Add((kv.Key, elem, p, true, siteName));
                }

            return type;
        }

        /// <summary>The target type <c>T</c> of the reference named <paramref name="name"/> declared on
        /// <paramref name="type"/> (walking base types) via <paramref name="refAttr"/> (the cross- or intra-site
        /// reference attribute), or null when the model declares no such reference of that kind.</summary>
        private static ITypeSymbol? ReferenceTarget(INamedTypeSymbol type, string name, INamedTypeSymbol? refAttr)
        {
            for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
                foreach (var a in t.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(a.AttributeClass?.OriginalDefinition, refAttr)
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

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>A resolved event's shape: its subscriber constraint (<c>TSub</c>), its payload (<c>null</c> when
        /// argless), and whether it carries a payload.</summary>
        private sealed class EventInfo
        {
            public ITypeSymbol? Sub;
            public ITypeSymbol? Payload;
            public bool Typed;
        }

        /// <summary>Resolves a declared event to its <see cref="EventInfo"/>, reporting SIM028 when the custom
        /// <c>Type</c> doesn't implement <c>ISimEvent&lt;…&gt;</c> or disagrees with the declared <c>ArgType</c>. A
        /// stock event (no <c>Type</c>) has subscriber <c>IModel</c> and payload = <c>ArgType</c> (or none).</summary>
        private EventInfo? ResolveEvent(EventDesc evt)
        {
            ITypeSymbol? argType = null;
            if (!string.IsNullOrWhiteSpace(evt.ArgType))
            {
                argType = _c.GetTypeByMetadataName(evt.ArgType!);
                if (argType is null) { Report(BadEventType, evt.Name, evt.ArgType!); return null; }
            }

            if (!string.IsNullOrWhiteSpace(evt.Type))
            {
                var et = _c.GetTypeByMetadataName(evt.Type!);
                if (et is null) { Report(BadEventType, evt.Name, evt.Type!); return null; }
                var (sub, payload, ok) = SimEventShape(et);
                // The type must be an ISimEvent; if an ArgType was also declared, the type's payload must match it.
                if (!ok || (argType is not null && (payload is null || !SymbolEqualityComparer.Default.Equals(payload, argType))))
                {
                    Report(BadEventType, evt.Name, evt.Type!);
                    return null;
                }
                return new EventInfo { Sub = sub, Payload = payload, Typed = payload is not null };
            }

            return new EventInfo { Sub = _iModel, Payload = argType, Typed = argType is not null };
        }

        /// <summary>The <c>(TSub, payload)</c> of the <c>ISimEvent&lt;TSub[,T]&gt;</c> that <paramref name="et"/>
        /// implements — payload null for the argless form; <c>ok</c> false if it implements neither.</summary>
        private (ITypeSymbol? Sub, ITypeSymbol? Payload, bool Ok) SimEventShape(INamedTypeSymbol et)
        {
            foreach (var i in et.AllInterfaces)
                if (SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, _iSimEvent2))
                    return (i.TypeArguments[0], i.TypeArguments[1], true);
            foreach (var i in et.AllInterfaces)
                if (SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, _iSimEvent1))
                    return (i.TypeArguments[0], null, true);
            return (null, null, false);
        }

        /// <summary>Validates one <c>Subscriptions</c> entry: a known event, an <c>[EventHandler]</c> void method whose
        /// signature fits the event's payload, and a subscriber assignable to the event's <c>TSub</c>.</summary>
        private void CheckSubscription(INamedTypeSymbol holderType, string instance, string handler, string eventName,
            Dictionary<string, EventInfo> events)
        {
            if (!events.TryGetValue(eventName, out var info)) { Report(UnknownEvent, handler, eventName); return; }

            var m = FindMethod(holderType, handler);
            if (m is null || !HasAttr(m, _eventHandlerAttr) || !m.ReturnsVoid) { Report(BadEventHandler, handler, holderType.Name); return; }

            if (!info.Typed)
            {
                if (m.Parameters.Length != 0) Report(HandlerSignature, handler, holderType.Name, eventName, "no parameters");
            }
            else if (m.Parameters.Length != 1 || (info.Payload is not null && !Assignable(info.Payload, m.Parameters[0].Type)))
            {
                Report(HandlerSignature, handler, holderType.Name, eventName, "a single parameter of '" + info.Payload?.ToDisplayString() + "'");
            }

            if (info.Sub is not null && !Assignable(holderType, info.Sub))
                Report(SubscriberKind, instance, eventName, holderType.ToDisplayString(), info.Sub.ToDisplayString());
        }

        /// <summary>Validates one <c>Triggers</c> entry: a known event and an <c>[EventReference]</c> member typed as the
        /// matching trigger-only invocator (<c>ISimEventInvocator</c> / <c>ISimEventInvocator&lt;T&gt;</c>).</summary>
        private void CheckTrigger(INamedTypeSymbol holderType, string member, string eventName, Dictionary<string, EventInfo> events)
        {
            if (!events.TryGetValue(eventName, out var info)) { Report(UnknownEvent, member, eventName); return; }

            var m = FindMember(holderType, member);
            var mt = MemberType(m) as INamedTypeSymbol;
            INamedTypeSymbol? expected = info.Typed
                ? (info.Payload is not null ? _iInvocator1?.Construct(info.Payload) : null)
                : _iInvocator;
            // The member must be an invocator interface (trigger-only) the shared event can be assigned to.
            bool ok = m is not null && HasAttr(m, _eventRefAttr) && mt is not null && expected is not null
                      && IsInvocator(mt) && Assignable(expected, mt);
            if (!ok) Report(BadEventReference, member, holderType.Name, ExpectedInvocator(info));
        }

        private bool IsInvocator(INamedTypeSymbol t) =>
            SymbolEqualityComparer.Default.Equals(t, _iInvocator)
            || SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, _iInvocator1);

        private static string ExpectedInvocator(EventInfo info) =>
            info.Typed ? "ISimEventInvocator<" + (info.Payload?.ToDisplayString() ?? "T") + ">" : "ISimEventInvocator";

        private static IMethodSymbol? FindMethod(INamedTypeSymbol type, string name)
        {
            for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
            {
                var m = t.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(static x => x.MethodKind == MethodKind.Ordinary);
                if (m is not null) return m;
            }
            return null;
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
        public readonly Dictionary<string, string> Events = new();               // event name → var
        // Event wiring, bound after all models exist: (referrer var, event name, [EventHandler] method / [EventReference] member)
        public readonly List<(string ModelVar, string EventName, string Handler)> Subs = new();
        public readonly List<(string ModelVar, string EventName, string Member)> Trigs = new();
        public readonly Dictionary<string, string> ModelsByPath = new();
        public readonly Dictionary<string, string> TypesByPath = new();
        public readonly Dictionary<string, string> SiteVarByPath = new();   // path → the site (scheduler) var it runs on
        public readonly List<string> ModelVars = new();
        public readonly List<(string ModelVar, string Member, string ServiceName)> Deps = new();
        // (referrer var, referrer's site var, field name, cross-ref element interface (FQ, "" for intra), targets, isCross)
        public readonly List<(string ModelVar, string CallerSite, string Field, string ElemInterface, List<string> Paths, bool IsCross)> Refs = new();

        public Compilation Compilation = null!;
        public INamedTypeSymbol? ContainsAttr;
        public INamedTypeSymbol? CrossRefAttr;

        /// <summary>The fully-qualified interface <c>T</c> of the <c>[CrossReference&lt;T&gt;("name")]</c> declared on
        /// <paramref name="parentType"/> (walking base types), or "" if the reference is not a cross-reference — in
        /// which case it is an intra-site <c>[Reference&lt;T&gt;]</c> (validation has guaranteed one of the two exists).</summary>
        public string CrossReferenceElementType(string parentType, string name)
        {
            for (INamedTypeSymbol? t = Compilation.GetTypeByMetadataName(parentType); t is not null; t = t.BaseType)
                foreach (var a in t.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(a.AttributeClass?.OriginalDefinition, CrossRefAttr)
                        && a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is string n && n == name
                        && a.AttributeClass is { TypeArguments.Length: 1 } ac)
                        return ac.TypeArguments[0].ToDisplayString(Fq);
            return "";
        }

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
        public List<EventDesc>? Events { get; set; }                             // global pub/sub events
        public List<SiteDesc>? Sites { get; set; }
    }

    private sealed class EventDesc
    {
        public string Name { get; set; } = "";
        public string? ArgType { get; set; }                                     // payload type; omit for an argless event
        public string? Type { get; set; }                                        // custom ISimEvent<…>; default SimEvent[<ArgType>]
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
        public List<Dictionary<string, List<string>>>? Refs { get; set; }        // intra-site [Reference<T>]
        public List<Dictionary<string, List<string>>>? CRefs { get; set; }       // cross-site [CrossReference<T>]
        public List<Dictionary<string, string>>? Deps { get; set; }              // service deps
        public List<Dictionary<string, string>>? Subscriptions { get; set; }     // { [EventHandler] method: eventName }
        public List<Dictionary<string, string>>? Triggers { get; set; }          // { [EventReference] member: eventName }
    }
}
