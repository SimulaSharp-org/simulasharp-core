using System.IO.Compression;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Hosting.Tests;

/// <summary>
/// Tests <see cref="SimulatorPackage"/> against the built <c>Hosting.Tests.Fixture</c> assembly
/// (which contains <c>FooSimulator : SimulatorBase</c>): discovery, creation through the shared
/// <see cref="ISimulator"/>, the <c>.nupkg</c> extract path, collectible unload, and reload.
/// </summary>
[TestFixture]
public sealed class SimulatorPackageTests
{
    private static string FixtureDll => typeof(FooSimulator).Assembly.Location;

    private string _temp = "";

    [SetUp]
    public void SetUp()
    {
        _temp = Path.Combine(Path.GetTempPath(), "simhost-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void Discovers_the_simulator_by_name_from_a_dll()
    {
        using var pkg = new SimulatorPackage(FixtureDll);
        Assert.That(pkg.Simulators, Does.Contain("Foo"));
    }

    [Test]
    public void Create_returns_a_working_simulator_through_the_shared_interface()
    {
        using var pkg = new SimulatorPackage(FixtureDll);
        ISimulator sim = pkg.Create("Foo");
        try
        {
            sim.Configure();
            sim.Start();
            Assert.That(sim.State, Is.EqualTo(SimulatorState.Running));
            sim.Stop();
        }
        finally { sim.Dispose(); }
    }

    [Test]
    public void Create_with_an_unknown_name_throws()
    {
        using var pkg = new SimulatorPackage(FixtureDll);
        Assert.That(() => pkg.Create("Nope"), Throws.ArgumentException);
    }

    [Test]
    public void Loads_from_a_nupkg()
    {
        string nupkg = BuildNupkg();
        using var pkg = new SimulatorPackage(nupkg);
        Assert.That(pkg.Simulators, Does.Contain("Foo"));
        pkg.Create("Foo").Dispose();
    }

    [Test]
    public void Dispose_unloads_the_context_and_removes_the_shadow()
    {
        string shadow = RunAndDispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.That(Directory.Exists(shadow), Is.False, "shadow (locked dll) not removed — the context did not unload");
    }

    [Test]
    public void Reloads_after_dispose()
    {
        using (var pkg = new SimulatorPackage(FixtureDll)) RunOne(pkg);
        using var reloaded = new SimulatorPackage(FixtureDll);
        Assert.DoesNotThrow(() => RunOne(reloaded));
    }

    [Test]
    public void A_missing_path_throws()
    {
        Assert.That(() => new SimulatorPackage(Path.Combine(_temp, "does-not-exist.dll")), Throws.Exception);
    }

    // Kept in a separate method so the created ISimulator holds no reference past return,
    // letting the collectible context be collected when the package is disposed.
    private static string RunAndDispose()
    {
        var pkg = new SimulatorPackage(FixtureDll);
        string shadow = pkg.ShadowDirectory;
        RunOne(pkg);
        pkg.Dispose();
        return shadow;
    }

    private static void RunOne(SimulatorPackage pkg)
    {
        var sim = pkg.Create("Foo");
        sim.Configure();
        sim.Start();
        sim.Stop();
        sim.Dispose();
    }

    private string BuildNupkg()
    {
        string id = Path.GetFileNameWithoutExtension(FixtureDll);
        string nupkg = Path.Combine(_temp, id + ".1.0.0.nupkg");
        using var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create);

        var nuspec = zip.CreateEntry(id + ".nuspec");
        using (var w = new StreamWriter(nuspec.Open()))
            w.Write($"<?xml version=\"1.0\"?><package><metadata><id>{id}</id><version>1.0.0</version></metadata></package>");

        zip.CreateEntryFromFile(FixtureDll, $"lib/net10.0/{id}.dll");
        return nupkg;
    }
}
