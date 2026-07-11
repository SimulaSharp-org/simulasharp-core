# SimulaSharp.Kernel

The hard-real-time simulation **runtime** for [SimulaSharp](https://www.nuget.org/packages/SimulaSharp). Referenced
transitively by the `SimulaSharp` meta-package — you normally don't reference this directly.

Provides: the single-writer `SimClock` (1:1 real time, park-on-idle sites woken precisely by the clock), `SimSite`
actors (one thread per subsystem, lock-free message passing), the allocation-free `EventQueue`, `SimYield` coroutines,
the model/service base types (`ModelBase`, `ServiceBase`, `LooselyCoupledServiceBase`), the `Container<T>`/`Reference<T>`
association primitives, and the observability surface (`Knob`, `Plottable`, `ParameterInfo`, structured logging) exposed
through `ISimulator`.

> Copyright (c) 2026 Andreas Michelis. Licensed under the MIT License. See the bundled LICENSE.
