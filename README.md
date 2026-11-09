# VFX Agent: let an AI agent build Unity VFX Graphs

**VFX Agent** (`com.fenyce.vfxagent`) is an editor-only Unity package that gives a coding agent (Claude Code, OpenCode,
or anything that can run a shell) full control of **Unity VFX Graph**. The agent can read, author, rewire, compile and
diagnose `.vfx` assets from the command line. It never hand-edits the YAML: every change goes through the VFX Graph
package's own editor model, the same code the VFX Graph window runs.

This repository contains the package and a **minimal Unity 6.6 URP demo project**: six short op-scripts that build
a set of demo effects from VFX Graph's own templates, a one-click builder that puts them in a scene, the regression
self-test, and the skill that teaches an agent how to use it.

```text
agent ──► unity CLI ──► Unity Editor ──► VfxGraphOps.Apply(script) ──► VFX Graph model ──► saved + compiled .vfx
                                      └─► VfxGraphTool.Describe(path) ◄── readable dump for the agent to inspect
```

---

## Contents

- [Why](#why)
- [What it can do](#what-it-can-do)
- [Requirements](#requirements)
- [Install](#install)
- [Try the demo](#try-the-demo)
- [Quick start](#quick-start)
- [The op-script language](#the-op-script-language)
- [Op reference](#op-reference)
- [Worked examples](#worked-examples)
- [Reading a graph with Describe](#reading-a-graph-with-describe)
- [Safety: rollback on error](#safety-rollback-on-error)
- [Verification: coverage and self-test](#verification-coverage-and-self-test)
- [Using it from an agent](#using-it-from-an-agent)
- [How it works](#how-it-works)
- [Known limits](#known-limits)
- [Troubleshooting](#troubleshooting)
- [Repository layout](#repository-layout)
- [Development workflow](#development-workflow)
- [Roadmap](#roadmap)
- [License](#license)

---

## Why

VFX Graph assets are big serialized YAML files full of cross-references. Editing them as text breaks them. The
VFX Graph window is the only safe editor, and an agent can't click through a window.

VFX Agent closes that gap. It drives the **graph model** that the window itself manipulates, through the VFX
package's internal editor API. An agent writes a short **op-script** (one edit per line), applies it in the running
Editor, and reads back a plain-text **description** of the result, including compile errors.

## What it can do

| Area | What the agent can do |
|---|---|
| **Read and compile** | Dump any graph: contexts, flow, blocks, settings, slot values, links, parameters, custom attributes, warnings and compile errors. Compile like the importer and report systems and shaders. |
| **Create** | Add any context, operator or block. Every entry of the VFX node library is reachable by the exact name the node search shows (`addvariant`): in VFX Graph 17.6.0, **18/18 contexts, 243/243 operators, 135/135 blocks** under URP, and **21/21, 244/244, 135/135** under HDRP. |
| **Edit** | Block, context and operator input values (including dotted sub-slots), model settings (enums by name, asset references), slot spaces, enable/disable blocks, Custom HLSL blocks from a file. |
| **Wire** | Data links between parameters, operators, blocks, contexts and subgraph outputs; flow links between contexts; remove single links or all links of an input. |
| **Remove** | Blocks, operators, contexts (unlinked first), parameters. |
| **Blackboard** | Create, rename and delete parameters; exposed on/off, default value, tooltip, Range min/max, Enum values, category, order; create, rename and delete categories; custom attributes (add, rename, reorder, delete). |
| **Subgraphs** | Create operator and block subgraph assets, define their inputs and outputs, use a subgraph as an operator, block or context, refresh the graphs that use a subgraph, and **convert a selection of operators into a subgraph operator** (boundary links become its interface). |
| **Clipboard** | Copy and paste operators, parameters, whole contexts with their blocks and data, and single blocks, inside one graph or across graphs, keeping values, settings and internal links. |
| **Safety** | A script that fails on any line rolls the graph back to its last good state, so a half-applied edit never stays behind. |
| **Verification** | Library coverage counter, and a 35-check regression self-test you can run in one call. |

## Requirements

- **Unity 6000.6** (tested on 6000.6.7f1). The package pins `"unity": "6000.6"`.
- **VFX Graph 17.6.0**, which ships with Unity 6000.6 and is declared as a dependency. Tested with **URP 17.6.0**
  (this demo) and **HDRP 17.6.0**; the self-test passes 35/35 on both.
- The **`unity` CLI** (package `com.unity.pipeline`, experimental; this demo uses `0.10.0-exp.1`) to drive the Editor from a shell. The package's C# does
  not depend on it; the CLI is only the transport.
- An agent that can run shell commands, for example **Claude Code** or **OpenCode**.

## Install

### From GitHub (git URL)

Add to your project's `Packages/manifest.json`:

```json
"com.fenyce.vfxagent": "https://github.com/LucaBiada/vfxagent.git?path=/Packages/com.fenyce.vfxagent"
```

Append `#<tag or commit>` to pin a version, for example `#v0.1.0`.

### From a local folder

Copy `Packages/com.fenyce.vfxagent/` into your project's `Packages/` folder. Unity picks up embedded packages
automatically, with no manifest entry needed. Alternatively, keep it anywhere and reference it:

```json
"com.fenyce.vfxagent": "file:../../path/to/com.fenyce.vfxagent"
```

### Check the install

```bash
unity command eval 'return UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("Unity.Testing.VisualEffectGraph.Editor");' --format json
```

The path must point into `Packages/com.fenyce.vfxagent/`. Then run the self-test (see below) and expect `35/35`.

> **Only one copy.** The package's assembly has a fixed name (see [How it works](#how-it-works)). If an older copy
> of the tool exists under `Assets/` (for example `Assets/VfxAuthoring/`), delete it first, in Edit mode. Otherwise Unity reports
> `Multiple assemblies with equivalent name 'Unity.Testing.VisualEffectGraph.Editor'`.

### Install the agent skill

The package ships a skill that documents every call and op: `Packages/com.fenyce.vfxagent/Skills~/vfx-graph/SKILL.md`.

- **Claude Code:** copy it to `<project>/.claude/skills/vfx-graph/SKILL.md`.
- **OpenCode:** copy it to `<project>/.opencode/skills/vfx-graph/SKILL.md`, or point the `skills` list of your
  `opencode.jsonc` at the folder.

## Try the demo

1. Clone this repository and open it with **Unity 6000.6** (Hub → Add project from disk).
2. Open `Assets/VfxAgentDemo/Demo.unity`: six effects in a row, each labelled.
3. Rebuild everything from the scripts at any time: **Tools → VFX Agent → Build Demo**, or from a shell:
   `unity command eval 'return VfxAgentDemoBuilder.BuildAll();' --format json`.

| Script (`Assets/VfxAgentDemo/Scripts/`) | Builds | Shows |
|---|---|---|
| `01_Fountain.vfxscript` | `Fountain.vfx` | exposed parameter, new block, blend mode |
| `02_Blackboard.vfxscript` | `Tunable.vfx` | categories, tooltip, Range, Enum, custom attribute |
| `03_Subgraph.vfxscript` | `Double.vfxoperator` + `UsesSubgraph.vfx` | building and using an operator subgraph |
| `04_Broken.vfxscript` | `Broken.vfx` | an effect that "shows nothing" |
| `05_Fixed.vfxscript` | `Fixed.vfx` | the two-line diagnosis and fix |
| `06_Clipboard.vfxscript` | `TwoSystems.vfx` | copying a whole system and a block, rewiring it to a spawner |

The scripts are short and commented: they're the fastest way to learn the op language.

## Quick start

All calls go through `unity command eval` in a running Editor. Put C# in a file to avoid shell-quoting problems:

```csharp
// describe.cs : print a readable dump of a graph (true = also compile it)
return VfxGraphTool.Describe("Assets/VFX/MyEffect.vfx", true);
```

```bash
unity command eval --file describe.cs --format json
```

Apply an op-script:

```text
# fountain.vfxscript
copy Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Loop.vfx Assets/VFX/Fountain.vfx
param float "Rate" 200
link "Rate" Spawn "Constant Spawn Rate" Rate
set Init "Velocity" MinSpeed=2 MaxSpeed=3
addblock Update Turbulence
setting Output blendMode=Additive
save
compile
```

```csharp
// apply.cs
return VfxGraphOps.Apply(System.IO.File.ReadAllText(@"C:/work/fountain.vfxscript"));
```

```bash
unity command eval --file apply.cs --format json
```

The result is a line-by-line log:

```text
1: copied -> Assets/VFX/Fountain.vfx
2: param float 'Rate' = 200
3: linked 'Rate' -> Spawn 'Constant Spawn Rate'.Rate
4: set Init '|Set|_Velocity from Direction & Speed|New Direction' MinSpeed=2 MaxSpeed=3
5: added block 'Turbulence' to Update
6: setting Output blendMode=Additive
7: saved Assets/VFX/Fountain.vfx
8: compile: success=True systems=2 shaders=4
```

> **The 5-second rule.** The CLI waits at most about 5 s for an eval. For long scripts or big graphs, write the output
> to a file inside the eval (`System.IO.File.WriteAllText(path, VfxGraphOps.Apply(script))`) or use
> `VfxGraphOps.ApplyToFile(scriptText, reportPath)`, then read the file.

> **Never in Play mode.** Don't apply scripts, save assets or compile while the Editor is in Play mode. Check
> `EditorApplication.isPlaying` first.

## The op-script language

- One op per line. `#` starts a comment. Blank lines are ignored.
- Quote anything with spaces: `"Constant Spawn Rate"`. A `key="value with spaces"` pair is kept together.
- The script works on one **target graph** at a time. `copy`, `open` and `newsubgraph` change the target; `save`
  writes it.

### Selectors

| Selector | Picks |
|---|---|
| `Spawn`, `Init`, `Update`, `Output` | the first context of that kind |
| `Output:VFXLitMeshOutput` | the first context of that .NET type |
| `Update@Trail`, `"Spawn@spawn Circle U"` | a context by its name or system title |
| `#id` | a context created earlier in the script (`addctx`, `addsubgraph ctx`, `clippaste`) |
| `"Velocity"` | a block whose name contains the text (exact name wins) |
| `"Over Life#1"` | the n-th matching block, **0-based** |
| `Multiply#1` | the n-th operator of that type (as in `setop`) |
| `arcSphere.sphere.radius` | a dotted sub-slot path |

### Link endpoints (`link2`, `unlink`, `setin`)

| Source | Destination |
|---|---|
| `P:<param>` (parameter output) | `Op:<id>.<slot>` (operator input) |
| `O:<id>` or `O:<id>.<slot>` (operator or subgraph output) | `B:<ctx>/<block>/<slot>` (block input) |
| | `C:<ctx>/<slot>` (context input) |
| | `P:<output param>` (a subgraph's output parameter) |

Quote a whole spec that contains spaces: `"B:Spawn/Constant Spawn Rate/Rate"`.

### Values

`0.3` · `12` · `true` · `(0,-4,0)` · `asset:Assets/Textures/Smoke.png` · `aabox(cx,cy,cz;sx,sy,sz)` ·
`gradient:white-fade` · `curve:0=1;0.7=0.5;1=0` (AnimationCurve keys `time=value`, smoothed). Enum settings take the
enum name (`blendMode=Additive`).

### Library names

`addvariant` takes the name the VFX node search shows, which carries markup such as `|Set|_Position` or
`Output Particle|HDRP Lit|Quad`. A wrong name returns the closest matches, so a first guess is cheap. When two entries
share a name (`And` exists as both a logical and a bitwise operator), add `#k`, 1-based: `And#2`.

## Op reference

### Target and files
| Op | Effect |
|---|---|
| `copy <src.vfx> <dst.vfx>` | copy an asset (replacing `dst`) and make the copy the target |
| `open <path>` | target an existing graph (`.vfx`, `.vfxoperator`, `.vfxblock`) |
| `save` | write the target with the package's own writer |
| `compile` | compile like the importer and log `success`, `systems`, `shaders` |
| `describe` | append a `Describe` dump of the target to the log |

### Create
| Op | Effect |
|---|---|
| `addvariant <ctx\|op\|block> "<library name>" <id\|ctx> [k=v ...]` | create any library entry by its node-search name |
| `addop <OperatorType> <id> [k=v ...]` | new operator by .NET type name; refer to it as `<id>` later |
| `addblock <ctx> <BlockType> [k=v ...]` | append a block by type name (settings first: they decide its slots) |
| `addctx <ContextType> <id> [k=v ...]` | new context; select it later as `#<id>` |

### Edit
| Op | Effect |
|---|---|
| `set <ctx> "<block>" <slot>=<v> ...` | block input values |
| `setctx <ctx> <slot>=<v> ...` | context input values |
| `setop <OperatorType>[#n] <slot>=<v> ...` | operator input values |
| `setin <dst> <v>` | one input slot, using link-endpoint syntax |
| `setting <ctx> ["<block>"] <name>=<v> ...` | model settings of a context or block |
| `opsetting <OperatorType[#n]> <name>=<v> ...` | operator settings |
| `enable` / `disable <ctx> "<block>"` | toggle a block |
| `space <ctx> "<block>" <slot> <None\|Local\|World>` | slot space |
| `hlsl <ctx> "<block>" file=<path> function=<name>` | add or update a Custom HLSL block from a `.hlsl` file |

### Wire
| Op | Effect |
|---|---|
| `link2 <src> <dst>` | data link (see endpoints above) |
| `unlink <dst>` | remove every link into that input |
| `unlink <src> <dst>` | remove that one link |
| `linkflow <fromCtx> <toCtx>` / `unlinkflow <fromCtx> <toCtx>` | flow link between contexts |
| `link "<param>" <ctx> "<block>" <slot>` | shortcut: parameter → block input |
| `linkop "<param>" <OperatorType> <slot>` / `linkctx "<param>" <ctx> <slot>` | shortcuts: parameter → operator / context input |

### Remove
| Op | Effect |
|---|---|
| `removeblock <ctx> "<block>"` | delete a block |
| `removeop <OperatorType[#n]\|#id>` | delete an operator and its links |
| `removectx <ctx>` | delete a context (flow and data links removed first) |
| `paramdelete "<name>"` | delete a parameter and its links |

### Blackboard
| Op | Effect |
|---|---|
| `param <type> "<name>" [value]` | create or update an exposed parameter (`float int bool vector3 mesh texture2d gradient curve skinnedmesh transform`) |
| `paramset "<name>" key=value ...` | `exposed=true\|false`, `tooltip="..."`, `category="..."`, `order=<n>`, `value=<v>`, `range=<min>;<max>`, `enum=A;B;C`, `filter=Default` |
| `paramrename "<old>" "<new>"` | rename (error if the new name exists) |
| `category add\|rename\|delete "<name>" ["<new>"]` | categories; *delete* moves members to the root (the window deletes them) |
| `attr add "<name>" <type> ["description"]` | custom attribute (`float int uint bool vector2 vector3 vector4`) |
| `attr rename\|delete\|order ...` | rename, delete or reorder a custom attribute |

### Subgraphs
| Op | Effect |
|---|---|
| `newsubgraph <operator\|block> <path>` | create an empty `.vfxoperator` or `.vfxblock` and make it the target |
| `subparam <in\|out> <type> "<name>" [default]` | add an input (exposed) or output (`isOutput`) parameter to the current subgraph |
| `addsubgraph op <asset> <id>` | use a subgraph as an operator |
| `addsubgraph block <ctx> <asset>` | insert a block subgraph into a context (compatibility checked) |
| `addsubgraph ctx <asset> <id>` | use a system subgraph (`.vfx`) as a context |
| `refreshparents <asset>` | re-prepare and re-import every graph that uses a subgraph (needed headless) |
| `tosubgraph <path.vfxoperator> <items...>` | convert selected operators/parameters into a new subgraph operator and replace them with one node (`sub1`) |

Self- and cyclic references are rejected with a clear error.

### Clipboard
| Op | Effect |
|---|---|
| `clipcopy <items...>` | items: `O:<id\|Type[#n]>`, `P:<param>`, `C:<ctx>`, `B:<ctx>/<block>`. The clipboard survives `open` and later calls |
| `clippaste [dx dy]` | paste operators, parameters and contexts into the target; new ids `paste1..`, `#pastectx1..` |
| `clippaste into <ctx>` | paste a blocks-only clipboard into a compatible context |

Pasting into the same graph reuses the copied parameters and keeps links coming in from outside. Pasting into
another graph reuses a parameter with the same name and type, or creates it.

## Worked examples

### Expose and tune parameters on the Blackboard

```text
copy Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Loop.vfx Assets/VFX/Tuned.vfx
param float "Rate" 64
link "Rate" Spawn "Constant Spawn Rate" Rate
category add "Spawn"
paramset "Rate" tooltip="Particles per second" range=0;500 category="Spawn" value=120
param int "Mode" 0
paramset "Mode" enum=Calm;Storm;Burst
attr add "Heat" float "particle temperature"
save
```

### Build an operator subgraph and use it

```text
newsubgraph operator Assets/VFX/Double.vfxoperator
subparam in float "X" 10
subparam out float "Y"
addop Add a1
link2 P:X Op:a1.a
link2 P:X Op:a1.b
link2 O:a1 P:Y
save
copy Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Loop.vfx Assets/VFX/UsesDouble.vfx
addsubgraph op Assets/VFX/Double.vfxoperator S1
setin Op:S1.X 40
link2 O:S1.Y "B:Spawn/Constant Spawn Rate/Rate"
save
```

### Turn existing operators into a subgraph

```text
open Assets/VFX/UsesMath.vfx
tosubgraph Assets/VFX/MathChunk.vfxoperator O:m1 O:a1
save
```

The log reports the new interface, for example `inputs=[K] outputs=[Rate]`. Incoming links become inputs named after
their source parameter; outgoing links become outputs named after the slot they fed.

### Copy a whole system into another graph

```text
open Assets/VFX/Source.vfx
clipcopy C:Init C:Update C:Output
copy Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Loop.vfx Assets/VFX/Target.vfx
clippaste 0 -600
save
```

### Fix a graph that "shows nothing"

```text
copy Assets/VFX/Broken.vfx Assets/VFX/Fixed.vfx
describe
```

Read the dump. It shows a spawn `Rate : Single = 0` and a lifetime of `0`, so you fix just those:

```text
open Assets/VFX/Fixed.vfx
set Spawn "Constant Spawn Rate" Rate=64
set Init "Lifetime" A=0.8 B=1.2
save
compile
```

A complete, commented sample is in `Packages/com.fenyce.vfxagent/Samples~/SimpleLoopExample.vfxscript`.

## Reading a graph with Describe

`VfxGraphTool.Describe(path, compile)` returns plain text built for an agent to read:

```text
# Assets/VFX/UsesDouble.vfx
compiled: OK

## Contexts (flow)
[C0] VFXBasicSpawner "Spawn system" type=Spawner data=VFXDataSpawner#9832
    flow in : [0: | 1:]   flow out: [0:C1.0]
    - block VFXSpawnerConstantRate "Constant Spawn Rate"
        in  Rate : Single = 64  <- O5.Y
...
## Exposed / graph parameters
[P4] EXPOSED 'Rate' : Single = 120
    exposed=True  category='Spawn'  order=0  tooltip='Particles per second'  filter=Range  min=0 max=500
        out o -> O6.Rate
## Custom attributes
  'Heat' : Float 'particle temperature'
## Operators
[O5] VFXSubgraphOperator "Double" m_Subgraph=Assets/VFX/Double.vfxoperator
    in  X : Single = 40
## Compilation errors
  (none)
```

`<- X` means "fed by X", `-> X` means "feeds X". Ids (`C0`, `P4`, `O5`) are only valid within one dump. Errors list the
valid names when a selector doesn't match, so the dump plus one failed try is usually enough to find any name.

## Safety: rollback on error

The interpreter snapshots the target graph with the VFX package's own `Backup()` when a graph becomes the target and
after every `save`. If any line fails, it restores that snapshot and reports:

```text
4: ERROR in 'set Spawn "NoSuchBlock" Rate=1': no block 'NoSuchBlock'#0 in Spawn; blocks: [Constant Spawn Rate]
stopped; rolled back to the state before line 2
```

Edits before the failing line are undone in memory. Anything already `save`d stays on disk, so put `save` at the end of
a script. (`tosubgraph` writes its new asset immediately; a rolled-back conversion leaves that file behind.)

## Verification: coverage and self-test

```csharp
return VfxSelfTest.Run(@"C:/work/selftest.md");   // -> "selftest: 35/35 checks passed"
return VfxCoverage.Run(@"C:/work/coverage.md");   // -> creation, reachability and wiring counts per kind
```

- **`VfxSelfTest`** builds fresh graphs under `Assets/VfxTests/SelfTest/` from the package template and checks 35
  expected results across 10 scenarios: Blackboard, errors, subgraphs, unlink/remove, rollback, flow, clipboard
  (operators and contexts) and `tosubgraph`. It's the gate for any change to the tool: when the interpreter was
  deliberately replaced with an older version, the score dropped to 19/35. It passes 35/35 on URP and HDRP.
- **`VfxCoverage`** creates every context, operator and block of the node library, reports which are reachable by
  type name and by library name, and tries to wire every operator's output into a compatible input
  (17.6.0, URP: 235 of 243 operators wired, 2 have no output, 6 need a block configured for their shape).

## Using it from an agent

The skill (`Skills~/vfx-graph/SKILL.md`) is written for agents. Add a few project rules on top:

- Drive Unity only through `unity command eval ... --format json`. With two Editors open, pass `--port <n>`
  (`unity status` lists them).
- Put C# in a `.cs` file and use `--file` instead of inline code.
- Never apply, save or compile in Play mode.
- Treat `Assets/Samples` (or any library folder) as read-only: `copy` first, then edit the copy.
- After changing the tool itself, run `VfxSelfTest.Run` and require a full pass.

A typical agent loop is: **Describe → write a small script → Apply → read the log → Describe/compile → repeat.**

## How it works

- **Internal API through a named assembly.** VFX Graph's editor model (`VFXGraph`, `VFXContext`, `VFXBlock`,
  `VFXSlot`, `VFXParameter`, `VFXLibrary`, ...) is `internal`. The VFX package grants access to a few test assemblies
  via `InternalsVisibleTo` (`com.unity.visualeffectgraph/Editor/PackageInfo.cs`). This package's editor assembly is
  named **`Unity.Testing.VisualEffectGraph.Editor`**, one of those names, so it can use the model directly. That is
  also why only one copy can exist per project.
- **Same code paths as the window.** Creation goes through `VFXLibrary` descriptors. Links go through
  `VFXSlot.Link`/`Unlink`, saving through `WriteAssetWithSubAssets`, compiling through the importer path. The
  Blackboard ops use `VFXParameter`'s setters and `VFXGraph`'s custom-attribute methods. The window's
  controller and GraphView layers (which this assembly cannot see) are never needed. Where the window only offered
  a UI-bound implementation (copy/paste, convert-to-subgraph), the tool has its own model-level port.
- **Headless-safe.** Parent graphs of a changed subgraph are refreshed explicitly (`refreshparents`), because the
  package's own refresh only runs while a VFX window is open.

## Known limits

- **Convert to subgraph works for operators only.** Block-subgraph conversion isn't done yet.
- **The clipboard skips groups and sticky notes.**
- **Experimental library entries** appear only when VFX Graph's "experimental operators" preference is on.
- **A rolled-back `tosubgraph`** leaves its new subgraph asset on disk.
- **Unity Undo** doesn't record script edits. Use the script's rollback, version control or `copy` to a new asset.
- **Tested only on Unity 6000.6.7f1 / VFX Graph 17.6.0 with URP and HDRP 17.6.0.** Other versions may differ in internal API.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Multiple assemblies with equivalent name 'Unity.Testing.VisualEffectGraph.Editor'` | An old copy of the tool is still in `Assets/`. Delete it in Edit mode. |
| `'VfxGraphOps' does not exist` in an eval | The package isn't installed or the project has compile errors. Check the assembly path (see [Install](#install)) and the Console. |
| The eval times out after ~5 s | The work is too long for one CLI call. Write results to a file inside the eval and read the file. |
| `Multiple Unity Editors are running` | Pass `--port <n>` or `--project-path <path>` on every command. |
| A quoted name is split apart | Quote the whole token: `"B:Spawn/Constant Spawn Rate/Rate"`, `key="value with spaces"`. |
| `no block named '...'` / `no ... named '...'` | Run `describe`, or read the error, which lists the valid names or the closest library names. |
| The Editor stops answering after a package change | It may be showing a dialog or be paused while unfocused. Bring the Editor window to the front. |
| Changes appear in the window only after reopening | The window caches the graph. Reopen it after a script, or close the graph before running one. |

## Repository layout

| Path | What |
|---|---|
| `Packages/com.fenyce.vfxagent/` | **the package**: `Editor/` (C#), `Skills~/` (agent skill), `Samples~/` (example op-script), `README.md`, `CHANGELOG.md`, `LICENSE.md` |
| `Assets/VfxAgentDemo/Scripts/` | the six demo op-scripts |
| `Assets/VfxAgentDemo/Effects/` | the effects they build (committed, so the demo works right after cloning) |
| `Assets/VfxAgentDemo/Editor/` | `VfxAgentDemoBuilder`: runs the scripts and builds `Demo.unity` |
| `Assets/VfxAgentDemo/Demo.unity` | the demo scene |
| `Assets/Scenes/`, `Assets/Settings/` | the URP template's default scene and pipeline assets |

## Development workflow

The tool was built by Claude Code (Anthropic) together with OpenCode models (DeepSeek and Kimi), with a strict loop:

1. A model gets a narrow task file (exact files and line ranges, expected behaviour) and edits a staging copy.
2. The result is reviewed, copied into `Packages/com.fenyce.vfxagent/Editor/`, compiled in the Editor (never in Play
   mode), and tested with real op-scripts.
3. **`VfxSelfTest.Run` must pass in full** before a change is kept.

When you add an op, follow the existing style in `VfxGraphOps.cs`: one `case` per op, one log line per op, errors by
throwing (so rollback applies). Document it in the header comment and in `SKILL.md`, then add a case to `VfxSelfTest`.

## Roadmap

- Convert selected blocks into a block subgraph.
- Groups and sticky notes in the clipboard.
- A window-parity matrix: every VFX Graph window action mapped to an op, with a measured score next to the library
  coverage.
- Optional move to the broader internals name `Unity.VisualEffectGraph.EditorTests`, which also sees Shader Graph and
  HDRP internals.
- A Claude Code plugin with the skill and a thin CLI wrapper.

## License

[MIT](LICENSE) © 2026 Luca Biada - Fenyce.

Unity, VFX Graph, URP and HDRP are Unity Technologies products; this project is not affiliated with or endorsed by
Unity Technologies. The demo effects are built in the Editor from the templates that ship inside the VFX Graph package;
no Unity sample content is redistributed here.
