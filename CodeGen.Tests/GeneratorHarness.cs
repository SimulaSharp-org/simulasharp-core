using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SimulaSharp.Kernel.Model;

namespace SimulaSharp.CodeGen.Tests;

/// <summary>The outcome of running a generator: the emitted sources and the reported diagnostics.</summary>
internal sealed record GenResult(
    IReadOnlyList<(string HintName, string Source)> Sources,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The concatenation of every emitted source (for substring assertions).</summary>
    public string AllSource => string.Join("\n", Sources.Select(s => s.Source));

    /// <summary>True if a diagnostic with <paramref name="id"/> was reported.</summary>
    public bool HasDiagnostic(string id) => Diagnostics.Any(d => d.Id == id);

    /// <summary>The reported diagnostic ids.</summary>
    public IEnumerable<string> Ids => Diagnostics.Select(d => d.Id);
}

/// <summary>An in-memory <see cref="AdditionalText"/> for feeding a <c>.simassembly.yaml</c> to a generator.</summary>
internal sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
{
    public override string Path { get; } = path;
    public override SourceText GetText(CancellationToken cancellationToken = default) =>
        SourceText.From(content);
}

/// <summary>
/// Compiles a source snippet against the real Kernel + framework references and runs one incremental
/// generator over it, returning the generated sources and diagnostics. A <c>.simassembly.yaml</c>
/// additional file is recognized by the generator via its extension (no options provider needed).
/// </summary>
internal static class GeneratorHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var refs = tpa.Split(System.IO.Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        // Ensure the Kernel is present even if trimmed from the TPA set.
        refs.Add(MetadataReference.CreateFromFile(typeof(ModelBase).Assembly.Location));
        return refs.ToImmutableArray();
    }

    public static GenResult Run(IIncrementalGenerator generator, string source, string? simAssemblyYaml = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var additional = simAssemblyYaml is null
            ? []
            : new AdditionalText[] { new InMemoryAdditionalText("Test.simassembly.yaml", simAssemblyYaml) };

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            additionalTexts: additional);

        driver = driver.RunGenerators(compilation);
        var run = driver.GetRunResult();

        var sources = run.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(g => (g.HintName, g.SourceText.ToString()))
            .ToList();
        var diagnostics = run.Results.SelectMany(r => r.Diagnostics).ToList();
        return new GenResult(sources, diagnostics);
    }
}
