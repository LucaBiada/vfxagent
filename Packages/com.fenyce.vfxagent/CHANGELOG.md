# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the version follows [Semantic Versioning](https://semver.org/).

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
- Verification: `VfxCoverage.Run` reports 21/21 contexts, 244/244 operators and 135/135 blocks creatable
  (236/244 operators wired); `VfxSelfTest.Run` runs 35 regression checks.
- `Skills~/vfx-graph/SKILL.md`, `Samples~/SimpleLoopExample.vfxscript`, and a `package.json` pinning Unity `6000.6`
  with `com.unity.visualeffectgraph` `17.6.0`.

### Notes
