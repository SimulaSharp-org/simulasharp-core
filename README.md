# SimulaSharp

Real-time, parallel discrete-event simulation for .NET: author models, wire them in YAML, and Roslyn generates the simulator.

SimulaSharp is a C#/.NET 10 framework for building **hard-real-time, discrete-event simulations** as parallel actors. You write *models* (and a few *services*), annotate the values you want to observe, control or tune with `[Knob]`, `[Plottable]` and `[Parameter]`, and declare the topology in a small YAML *assembly*. The source generator compiles all of it into a strongly-typed, observable `<Name>Simulator`, with no runtime reflection.

## Packages

| Package | What it is |
| --- | --- |
| `SimulaSharp` | The meta-package. Reference this one: it brings in the Kernel and the CodeGen analyzer. |
| `SimulaSharp.Kernel` | The runtime: clock, sites, event queue, coroutines, services, knobs, plottables, parameters, logging. |
| `SimulaSharp.CodeGen` | The Roslyn source generators and diagnostics (`SIM001` and up). |
| `SimulaSharp.Hosting` | Packs a built simulator as a `.nupkg` and loads it into a collectible load context, so a host can load and reload it. |

```xml
<PackageReference Include="SimulaSharp" Version="0.5.3" />
```

The packages are not on nuget.org yet. Until they are, build them into a local folder feed (see below) and point your project at it; `nuget.config.sample` shows how.

## Repository layout

- `Kernel/`, `CodeGen/`, `Hosting/`: the libraries.
- `SimulaSharp/`: the meta-package project.
- `Kernel.Tests/`, `CodeGen.Tests/`, `Hosting.Tests/` (+ `Hosting.Tests.Fixture/`): NUnit tests.
- `SimulaSharp.slnx`: the solution (Kernel + CodeGen).
- `pack.ps1`: packs all packages at the version in `Directory.Build.props`.

## Build, test, pack

```powershell
dotnet build SimulaSharp.slnx
dotnet test Kernel.Tests\Kernel.Tests.csproj
dotnet test CodeGen.Tests\CodeGen.Tests.csproj
dotnet test Hosting.Tests\Hosting.Tests.csproj
pwsh -File pack.ps1            # writes the .nupkg/.snupkg files to .\artifacts
```

## Branches

`devel` is where development happens. `main` always points at the latest release.

## License

MIT. Copyright (c) 2026 Andreas Michelis. See [LICENSE](LICENSE).
