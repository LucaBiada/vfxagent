# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the version follows [Semantic Versioning](https://semver.org/).

## [0.1.3] - 2026-11-18

Lessons from a particle lab in a real project (a 4M-particle GPU dust field with emergent filaments, touch ripples,
floor collision and twinkle; a restyled ember graph with inward homing; skinned-mesh points baked to a texture).

### Added
- `param vector4` and `param color`; Color / Vector4 values `(r,g,b,a)` or `(r,g,b)` (a = 1, HDR allowed) in `param`,
  `paramset value=`, `set` and `setin`.
- `hlsl ... relink=byname|none`; the log names each input's source (`_Gain<-P:Gain`) and the old links that were dropped.
- `unlink?`: like `unlink`, but nothing to unlink is logged instead of stopping (and rolling back) the script.
- `paramlist [unused]` and `paramprune [disabled]`: list / delete input parameters linked to nothing (or only to
  disabled blocks).
- `VfxGraphTool.RunPendingJobs()`: runs pending `...ToFile` jobs at once.
- Runtime assembly `Fenyce.VfxAgent.Runtime` with `VfxPropertyHold`: re-applies exposed values every LateUpdate, since
  a VisualEffect drops values set from code when it is disabled and enabled again.
- Skill: `RECIPES.md` (hash randomness, start fully formed, individual drift, galaxy clumps, emergent filaments, touch
  ripples, floor collision, twinkle, skinned points for several bodies, restyle without behaviour change, homing after
  Turbulence, shader + VFX sharing one edge, holding runtime values, budget) and new Lessons.

### Fixed
- `hlsl` on an existing block with a changed signature left inputs linked by POSITION to the old parameters while the
  log said `(linked)`: every input is now unlinked and relinked by name.
- `ApplyToFile` / `DescribeToFile` / `CompileReportToFile` / `ForceReimportToFile` reports that never appeared: jobs are
  kept in SessionState (survive a domain reload), run from `EditorApplication.update` and `delayCall`, wake the editor
  loop, and always write their file (`CompileReportToFile` used to throw before its try, writing nothing). The calls and
  their return text are unchanged.

### Changed
- `describe`: an input fed by a parameter shows `<- P:<name>` instead of `<- P<n>.o`.
- Self-test: 23 new checks (69 in total, 69/69 on URP 17.6.0).

## [0.1.2] - 2026-11-16

Lessons from building a data-driven particle-strip effect (rope strands drawn as strips from C# data).

### Added
- `gpucheck`: compiles the graph and reports the GPU shader errors of its compute kernels, which the compile report
  never shows (each generated kernel compiled as a temporary .compute asset; kernels queried so the lazy compile runs).
- `param graphicsbuffer`: expose a GraphicsBuffer (set it with `VisualEffect.SetGraphicsBuffer`).
- `compile` also lists every model validation error (`INVALID [...]`), as Describe's `!!` lines and the window's badges.

### Changed
- `hlsl` imports a .hlsl file written to disk in the same session instead of failing with "no ShaderInclude".
- `hlsl` fails (rolling the script back) when the new block reports a validation error, e.g. a non-writable attribute:
  such a block was silently skipped at compile time.
- `hlsl` warns when the file defines several attribute functions (each block compiles the whole file in its context).
- A missing `<Kind>@<title>` selector lists system titles too and explains that removing the Init first drops them.
- Skill: new Lessons on particle strips (strip index via the Initialize input), looping bursts, per-point data from C#,
  one HLSL function per file, lazy compute shader messages.
- Self-test: 7 new checks (46 in total).

## [0.1.1] - 2026-11-09

### Added
- `dataspace <ctx> <Local|World>`: set a particle system's coordinate space.
- Link endpoints (`link2`, `unlink`, `setin`) accept an existing operator by type (`O:Multiply`, `Op:Multiply#1.b`), not only ids made earlier in the script.
- Self-test: 4 new checks (39 in total).

## [0.1.0] - 2026-11-09

### Added
- First installable UPM packaging of the VFX authoring tool (`com.fenyce.vfxagent`), with the editor-only assembly
  `Unity.Testing.VisualEffectGraph.Editor` kept from the prototype.
- `VfxGraphOps` op-script interpreter (`ApplyToFile` / `Apply`) with whole-script rollback in memory on the first
  failing line.
- Read/compile: `Describe` / `DescribeToFile`, `CompileReportToFile`, `ForceReimportToFile`, `FixOwnerOrder`.
- Create and edit: `addop` / `addblock` / `addctx` / `addvariant`, `set` / `setctx` / `setop` / `setin` /
  `setting` / `opsetting`, `enable` / `disable`, `space`, `hlsl`.
- Wiring and removal: `link2` / `unlink` / `linkflow` / `unlinkflow`; `removeblock` / `removeop` / `removectx` /
  `paramdelete`.
- Blackboard (`param`, `paramset`, `paramrename`, `category`, `attr`) and subgraphs (`newsubgraph`, `subparam`,
  `addsubgraph`, `refreshparents`, `tosubgraph`).
- Clipboard (`clipcopy` / `clippaste`) for contexts, blocks, operators and parameters, across graphs and `Apply` calls.
- Verification: `VfxCoverage.Run` reports every library entry creatable: URP 18/18 contexts, 243/243 operators,
  135/135 blocks (235/243 operators wired); HDRP 21/21, 244/244, 135/135 (236/244 wired). `VfxSelfTest.Run` runs
  35 regression checks and passes 35/35 on URP and HDRP.
- `Skills~/vfx-graph/SKILL.md`, `Samples~/SimpleLoopExample.vfxscript`, and a `package.json` pinning Unity `6000.6`
  with `com.unity.visualeffectgraph` `17.6.0`.
