using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Hosting;

/// <summary>
/// Loads a built simulator pack into a <em>collectible</em> <see cref="AssemblyLoadContext"/> so
/// the MMI can load and later reload it without bouncing the host. How it works:
/// the pack's payload is shadow-copied to a temp folder (so the original build can be rebuilt
/// while a copy is loaded), the <c>SimulaSharp.*</c> framework assemblies are shared from the
/// Default context (so <see cref="ISimulator"/>/<c>Knob</c>/<c>Plottable</c>/<c>LogEntry</c> have
/// a single type identity across the boundary), and only the pack's own code loads privately.
/// <para>
/// Accepts a <c>.nupkg</c> (produced by <c>dotnet pack</c>; its <c>lib/&lt;tfm&gt;/</c> payload is
/// extracted), a build-output <em>directory</em>, or a single <c>.dll</c>. Simulators are
/// discovered by scanning for public, non-abstract <see cref="SimulatorBase"/> subclasses with a
/// parameterless constructor — the generated <c>&lt;Name&gt;Simulator</c> types.
/// </para>
/// <para>
/// <b>Reload recipe (single-use aligned):</b> dispose the <see cref="ISimulator"/> you created,
/// then dispose this <see cref="SimulatorPackage"/> (unloads the ALC), then construct a new
/// <see cref="SimulatorPackage"/> on the new build. There is no in-place model swap — a fresh
/// instance replaces the old one.
/// </para>
/// </summary>
public sealed class SimulatorPackage : IDisposable
{
    /// <summary>
    /// A collectible context that shares the framework from Default and resolves the pack's
    /// private dependencies from its own folder via the deps.json-driven resolver.
    /// </summary>
    private sealed class PackLoadContext(string mainAssemblyPath)
        : AssemblyLoadContext("SimulaSharpPack", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Share every SimulaSharp.* assembly (Kernel, Hosting) from Default — type identity
            // must match across the boundary. Everything else loads from the pack's own folder.
            if (name.Name?.StartsWith("SimulaSharp", StringComparison.Ordinal) ?? false)
                return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }

    private readonly string _shadowDir;
    private readonly Dictionary<string, Type> _entries;
    private PackLoadContext? _alc;

    /// <summary>The source path this pack was loaded from (the <c>.nupkg</c>, folder, or dll).</summary>
    public string SourcePath { get; }

    /// <summary>The temp folder the pack payload was shadow-copied into; removed on <see cref="Dispose"/> once unloaded.</summary>
    public string ShadowDirectory => _shadowDir;

    /// <summary>The names of the simulators discovered in the pack (the <c>&lt;Name&gt;</c> of each <c>&lt;Name&gt;Simulator</c>).</summary>
    public IReadOnlyList<string> Simulators { get; }

    /// <summary>Loads the pack at <paramref name="path"/> (a <c>.nupkg</c>, a build-output directory, or a <c>.dll</c>).</summary>
    public SimulatorPackage(string path)
    {
        SourcePath = Path.GetFullPath(path);

        _shadowDir = Path.Combine(Path.GetTempPath(), "simulasharp-pack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_shadowDir);
        string mainDll = Materialize(SourcePath, _shadowDir);

        _alc = new PackLoadContext(mainDll);
        Assembly asm = _alc.LoadFromAssemblyPath(mainDll);

        _entries = asm.GetExportedTypes()
            .Where(static t => t is { IsAbstract: false, IsInterface: false }
                               && typeof(SimulatorBase).IsAssignableFrom(t)
                               && t.GetConstructor(Type.EmptyTypes) is not null)
            .ToDictionary(NameOf, static t => t, StringComparer.OrdinalIgnoreCase);
        Simulators = _entries.Keys.ToArray();
    }

    /// <summary>
    /// Creates a fresh instance of the named simulator. The caller owns its lifecycle
    /// (Initialize → Configure → Start/Stop → Dispose); dispose it before disposing this pack.
    /// </summary>
    /// <exception cref="ArgumentException">No simulator with that name was found in the pack.</exception>
    /// <exception cref="ObjectDisposedException">The pack has been unloaded.</exception>
    public ISimulator Create(string name)
    {
        ObjectDisposedException.ThrowIf(_alc is null, this);
        if (!_entries.TryGetValue(name, out var type))
            throw new ArgumentException($"no simulator '{name}' in pack (have: {string.Join(", ", Simulators)})", nameof(name));
        return (ISimulator)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Unloads the pack's assembly context and removes the shadow copy. Any <see cref="ISimulator"/>
    /// still referenced keeps the context alive until it is disposed and collected.
    /// </summary>
    public void Dispose()
    {
        var alc = _alc;
        _alc = null;
        alc?.Unload();
        _entries.Clear();   // drop the child-ALC Type references that would otherwise pin the context

        // A collectible context is reclaimed once the GC collects the last reference into it; nudge
        // it, then remove the shadow. On Windows the shadow dll stays locked until the unload
        // completes, so a successful delete is also evidence the context unloaded.
        for (int i = 0; i < 10 && Directory.Exists(_shadowDir); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(_shadowDir, recursive: true); break; }
            catch { Thread.Sleep(50); }
        }
    }

    /// <summary>The display name of a <c>&lt;Name&gt;Simulator</c> type — the type name minus a trailing <c>Simulator</c>.</summary>
    private static string NameOf(Type t) =>
        t.Name.EndsWith("Simulator", StringComparison.Ordinal) && t.Name.Length > "Simulator".Length
            ? t.Name[..^"Simulator".Length]
            : t.Name;

    /// <summary>
    /// Copies the pack payload into <paramref name="shadowDir"/> and returns the main assembly path.
    /// Handles a <c>.nupkg</c> (extract <c>lib/&lt;tfm&gt;/</c>), a directory (shadow-copy, main =
    /// the assembly beside the single <c>*.deps.json</c>), or a single <c>.dll</c> (shadow-copy its folder).
    /// </summary>
    private static string Materialize(string source, string shadowDir)
    {
        string ext = Path.GetExtension(source);

        if (string.Equals(ext, ".nupkg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".simpack", StringComparison.OrdinalIgnoreCase))
            return ExtractPackage(source, shadowDir);

        string srcDir = Directory.Exists(source) ? source : Path.GetDirectoryName(source)!;
        foreach (var f in Directory.GetFiles(srcDir))
            File.Copy(f, Path.Combine(shadowDir, Path.GetFileName(f)), overwrite: true);

        // A single .dll source names the main assembly directly; a directory infers it from deps.json.
        string mainName = Directory.Exists(source)
            ? DepsJsonBaseName(SingleDepsJson(shadowDir)) + ".dll"
            : Path.GetFileName(source);
        return Path.Combine(shadowDir, mainName);
    }

    /// <summary>Extracts a NuGet package's <c>lib/&lt;tfm&gt;/</c> payload into <paramref name="shadowDir"/>; returns the main dll (from the nuspec id).</summary>
    private static string ExtractPackage(string nupkg, string shadowDir)
    {
        using var zip = ZipFile.OpenRead(nupkg);

        // The nuspec id names the primary assembly (<id>.dll).
        var nuspec = zip.Entries.FirstOrDefault(e =>
            !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"pack '{Path.GetFileName(nupkg)}' has no .nuspec");
        string id;
        using (var s = nuspec.Open())
            id = XDocument.Load(s).Descendants().First(e => e.Name.LocalName == "id").Value;

        // Flatten the single lib/<tfm>/ folder into the shadow dir.
        var libEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0)
            .ToArray();
        if (libEntries.Length == 0)
            throw new InvalidOperationException($"pack '{Path.GetFileName(nupkg)}' has no lib/<tfm>/ payload");
        foreach (var e in libEntries)
            e.ExtractToFile(Path.Combine(shadowDir, e.Name), overwrite: true);

        return Path.Combine(shadowDir, id + ".dll");
    }

    private static string SingleDepsJson(string dir)
    {
        var deps = Directory.GetFiles(dir, "*.deps.json");
        return deps.Length == 1
            ? deps[0]
            : throw new InvalidOperationException($"expected exactly one *.deps.json in '{dir}', found {deps.Length}");
    }

    /// <summary>The assembly base name for a <c>&lt;name&gt;.deps.json</c> path (strips the full <c>.deps.json</c> suffix).</summary>
    private static string DepsJsonBaseName(string depsJsonPath)
    {
        string file = Path.GetFileName(depsJsonPath);
        return file[..^".deps.json".Length];
    }
}
