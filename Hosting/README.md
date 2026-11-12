![SimulaSharp logo](https://raw.githubusercontent.com/SimulaSharp-org/simulasharp-core/main/assets/icon.png)

# SimulaSharp.Hosting

The **loader** for packed [SimulaSharp](https://www.nuget.org/packages/SimulaSharp) simulations — for a runner/host (a
CLI or the MMI), not for authoring a simulation.

`SimulatorPackage` loads a built simulation into a **collectible `AssemblyLoadContext`**, sharing the `SimulaSharp.*`
assemblies from the host for a single type identity across the boundary, discovers the `SimulatorBase` subclasses
inside, creates one as an `ISimulator`, and can unload and reload it without restarting the host.

> Copyright (c) 2026 Andreas Michelis. Licensed under the MIT License. See the bundled LICENSE.
