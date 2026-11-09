# VFX Agent demo

A minimal Unity 6 URP project that shows [`com.fenyce.vfxagent`](../../Packages/com.fenyce.vfxagent/) building VFX Graphs
from short text scripts. Every graph in `Effects/` is generated from the op-scripts in `Scripts/`; the `.vfx` files are
never hand-edited.

| Script | Generates | What it shows |
|---|---|---|
| `01_Fountain.vfxscript` | `Effects/Fountain.vfx` | exposed `Rate` parameter linked into Spawn, narrow 2..3 m/s velocity, Turbulence, Additive blend |
| `02_Blackboard.vfxscript` | `Effects/Tunable.vfx` | Blackboard categories, tooltips, a 0..500 Range, an enum int `Mode`, custom attribute `heat` |
| `03_Subgraph.vfxscript` | `Effects/Double.vfxoperator`, `Effects/UsesSubgraph.vfx` | an operator subgraph (`Y = X + X`) used to feed a Spawn rate |
| `04_Broken.vfxscript` | `Effects/Broken.vfx` | a graph that shows nothing: spawn `Rate=0` and lifetime `A=B=0` |
| `05_Fixed.vfxscript` | `Effects/Fixed.vfx` | `describe` reveals those two causes, then both values are repaired |
| `06_Clipboard.vfxscript` | `Effects/TwoSystems.vfx` | copy/paste a whole system (offset 0 -600) and paste a `Linear Drag` block into the copy |

`Broken.vfx` is intentionally invisible; `Fixed.vfx` is the repaired copy of it.

## Rebuild

- In Unity: **Tools > VFX Agent > Build Demo**.
- From the `unity` CLI, run `return VfxAgentDemoBuilder.BuildAll();` (for example via
  `unity command eval --file <a .cs file containing that line> --format json`).

The builder runs the scripts in name order, regenerates `Effects/`, overwrites `Demo.unity` (camera, light and one
`VisualEffect` + 3D label per effect), and writes every op line and compile result to `BuildLog.txt`. Run it in Edit
mode only: it refuses to run in Play mode.

Op-script syntax reference: `Packages/com.fenyce.vfxagent/Skills~/vfx-graph/SKILL.md`.
