# Engine adapter boundary

`Systemic.Kernel` and `Systemic.Core` are engine-neutral. A client adapter owns the fixed-step clock bridge, input translation, presentation rendering, and persistence IO. It should consume the host and optional module APIs without introducing Godot types into Kernel or Core.

This repository contains only the engine-neutral contracts. Engine adapters live in the consuming game projects; no renderer or engine runtime package is introduced here.
