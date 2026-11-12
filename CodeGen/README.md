<img src="https://raw.githubusercontent.com/SimulaSharp-org/simulasharp-core/main/assets/icon.png" alt="SimulaSharp logo" width="64" align="right" />

# SimulaSharp.CodeGen

The Roslyn **source generator / analyzer** for [SimulaSharp](https://www.nuget.org/packages/SimulaSharp). Referenced
transitively by the `SimulaSharp` meta-package (as an analyzer / development dependency) — you normally don't reference
this directly.

It emits, at build time and against your real types:

- a strongly-typed `<Name>Simulator` from each `*.simassembly.yaml` (declared via the `<SimAssembly>` MSBuild item),
- the `[Knob]`, `[Plottable]`, `[Parameter]`, `[ServiceEvent]`/`[ServiceRequest]`/`[ServiceRef]` plumbing,
- the `<Type>Ref` accessors over `Reference<T>`,

with strict `SIM0xx` topology diagnostics reported at build time. No runtime reflection or YAML parsing ships in your
simulation.

> Copyright (c) 2026 Andreas Michelis. Licensed under the MIT License. See the bundled LICENSE.
