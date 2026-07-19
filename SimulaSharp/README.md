# SimulaSharp

The one package to reference when authoring a SimulaSharp simulation. It pulls in:

- **`SimulaSharp.Kernel`** — the hard-real-time simulation runtime (clock, SimSites, event queue, coroutines, services, knobs/plottables/parameters).
- **`SimulaSharp.CodeGen`** — the Roslyn analyzer/source-generator that turns your annotated models + a YAML *assembly* into a strongly-typed `<Name>Simulator`.

```xml
<PackageReference Include="SimulaSharp" Version="0.4.0" />
```

SimulaSharp is a C#/.NET framework for building **hard-real-time, discrete-event simulations** as parallel actors: you
write *models* (and a few *services*), annotate the values you want to observe/control/tune with `[Knob]`/`[Plottable]`/
`[Parameter]`, declare the topology in a small YAML assembly, and the generator compiles it into a runnable, observable
simulator — no runtime reflection.

> Copyright (c) 2026 Andreas Michelis. Licensed under the MIT License. See the bundled LICENSE.
