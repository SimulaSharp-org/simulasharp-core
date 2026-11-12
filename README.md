# SimulaSharp

Real-time, parallel discrete-event simulation for .NET: author models, wire them in YAML, and Roslyn generates the simulator.

[![CI](https://github.com/SimulaSharp-org/simulasharp-core/actions/workflows/ci.yml/badge.svg?branch=devel)](https://github.com/SimulaSharp-org/simulasharp-core/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/SimulaSharp.svg?logo=nuget)](https://www.nuget.org/packages/SimulaSharp)
[![Downloads](https://img.shields.io/nuget/dt/SimulaSharp.svg)](https://www.nuget.org/packages/SimulaSharp)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-simulasharp.net-blue.svg)](https://simulasharp.net)

SimulaSharp is a C#/.NET 10 framework for building **hard-real-time, discrete-event simulations** as parallel actors. You write *models* (and a few *services*), annotate the values you want to observe, control or tune with `[Knob]`, `[Plottable]` and `[Parameter]`, and declare the topology in a small YAML *assembly*. The source generator compiles all of it into a strongly-typed, observable `<Name>Simulator`, with no runtime reflection.

- **Real time, in parallel.** Each site runs on its own thread; one clock drives them all and wakes idle sites exactly when their next event is due.
- **Generated, not reflected.** Topology, wiring, references and the observability surface are emitted at build time, and a bad topology is a build error (`SIM001` and up), not a runtime surprise.
- **Observable by design.** Knobs, plottables and parameters are discovered per instance and exposed through `ISimulator`, ready for a host UI.
- **Packable.** A built simulator packs into a `.nupkg` that a host loads and unloads in a collectible load context.

## Install

```shell
dotnet add package SimulaSharp
```

The one `SimulaSharp` meta-package brings the runtime and the source generator, including the `<SimAssembly>` build item.

## Quick start

A model that logs a tick once a second:

```csharp
using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Model;
using SimulaSharp.Kernel.Services;

namespace Hello;

public sealed partial class Heartbeat(IClock c, IScheduler s, ILogger l) : ModelBase(c, s, l)
{
    // Called once, after the simulator is built and wired.
    public override void Configure()
        => Scheduler.AddPeriodicEvent(Tick, periodUs: 1_000_000);

    private void Tick()
        => Logger.LogInformation($"tick @ {TimeKeeper.SimNowUs} us");
}
```

The topology, in `hello.simassembly.yaml`. The `Name` becomes the generated class `HelloSimulator`:

```yaml
Simulator:
  Name: Hello
  Sites:
    - Name: Main
      Models:
        - Name: Beat
          Type: Hello.Heartbeat
```

Register the YAML in the project file:

```xml
<ItemGroup>
  <PackageReference Include="SimulaSharp" Version="0.5.3" />
  <SimAssembly Include="hello.simassembly.yaml" />
</ItemGroup>
```

Run it for three seconds, with the log routed to the console:

```csharp
using Hello;
using SimulaSharp.Kernel.Logging;

using var sim = new HelloSimulator();
sim.RegisterLogSink(new ConsoleSink());
sim.Configure();
sim.Start();
sim.RunFor(3_000_000);   // ~3 s of real time, then pause

sealed class ConsoleSink : ILogSink
{
    public void Submit(in LogEntry e) => Console.WriteLine($"[{e.Issuer}] {e.Message}");
}
```

```text
[Hello.Beat] tick @ 1001650 us
[Hello.Beat] tick @ 2000053 us
[Hello.Beat] tick @ 3000012 us
```

The [documentation](https://simulasharp.net) takes it from here: time and the clock, sites and dispatch, associations, services, global events, observability, packing and hosting, plus worked scenarios.

## Packages

| Package | What it is |
| --- | --- |
| [`SimulaSharp`](https://www.nuget.org/packages/SimulaSharp) | The meta-package. Reference this one: it brings in the Kernel and the CodeGen analyzer. |
| [`SimulaSharp.Kernel`](https://www.nuget.org/packages/SimulaSharp.Kernel) | The runtime: clock, sites, event queue, coroutines, services, knobs, plottables, parameters, logging. |
| [`SimulaSharp.CodeGen`](https://www.nuget.org/packages/SimulaSharp.CodeGen) | The Roslyn source generators and diagnostics (`SIM001` and up). |
| [`SimulaSharp.Hosting`](https://www.nuget.org/packages/SimulaSharp.Hosting) | Loads a packed simulator into a collectible load context, so a host can load and reload it. |

## The SimulaSharp repositories

- [simulasharp-core](https://github.com/SimulaSharp-org/simulasharp-core): this repo, the framework.
- [simulasharp-docs](https://github.com/SimulaSharp-org/simulasharp-docs): the documentation site, published at [simulasharp.net](https://simulasharp.net).
- [simulasharp-studio](https://github.com/SimulaSharp-org/simulasharp-studio): a visual design tool for models and topologies.

## Build from source

```shell
dotnet build SimulaSharp.slnx
dotnet test Kernel.Tests/Kernel.Tests.csproj
dotnet test CodeGen.Tests/CodeGen.Tests.csproj
dotnet test Hosting.Tests/Hosting.Tests.csproj
pwsh -File pack.ps1            # writes the .nupkg/.snupkg files to ./artifacts
```

Layout: `Kernel/`, `CodeGen/` and `Hosting/` are the libraries, `SimulaSharp/` is the meta-package, and `*.Tests/` are the NUnit tests. `SimulaSharp.slnx` holds Kernel and CodeGen. The version for every package is set once, in `Directory.Build.props`.

## Contributing

Development happens on `devel`, through pull requests from short-lived work branches. `main` only takes merges from `devel` and always holds the latest release; each merge into `main` publishes that version to nuget.org and creates the GitHub release. Bug reports and ideas go to the [issue tracker](https://github.com/SimulaSharp-org/simulasharp-core/issues).

## License

MIT. Copyright (c) 2026 Andreas Michelis. See [LICENSE](LICENSE).
