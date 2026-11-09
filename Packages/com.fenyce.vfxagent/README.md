# VFX Agent (`com.fenyce.vfxagent`)

Editor-only Unity package (UPM) that exposes the **VFX Graph package's internal editor API** so an agent
(Claude Code / OpenCode) can inspect, author, compile and diagnose Unity VFX Graph (`.vfx`) assets from the
`unity` CLI — without ever hand-editing the YAML.

This package is the current prototype (`VfxGraphTool`, `VfxGraphOps`, `VfxCoverage`) repackaged as an installable
package, plus the `vfx-graph` skill and one worked example op-script.

## What it can do

Grouped by task. The op list in the header of `Editor/VfxGraphOps.cs` is the source of truth; the skill explains
each op.

- **Read / compile** — `Describe` (contexts, blocks, slots, links, parameters, errors), `CompileReportToFile`,
  `ForceReimportToFile`, `FixOwnerOrder`; the `describe` / `compile` ops inside a script.
- **Create** — `addop`, `addblock`, `addctx`, `addvariant`.
- **Edit values & settings** — `set`, `setctx`, `setop`, `setin`, `setting`, `opsetting`, `enable` / `disable`,
  `space`, `hlsl`.
- **Wiring** — `link2`, `unlink`, `linkflow`, `unlinkflow` (parameter shortcuts: `link`, `linkop`, `linkctx`).
- **Remove** — `removeblock`, `removeop`, `removectx`, `paramdelete`.
- **Blackboard** — `param`, `paramset`, `paramrename`, `category`, `attr`.
- **Subgraphs** — `newsubgraph`, `subparam`, `addsubgraph`, `refreshparents`, `tosubgraph`.
- **Clipboard** — `clipcopy`, `clippaste` (contexts, blocks, operators, parameters; survives `open`).
- **Safety** — a failing line stops the script and rolls the whole graph back (in memory) to the last checkpoint.
- **Verification** — `VfxCoverage.Run` (library coverage: 21/21 contexts, 244/244 operators, 135/135 blocks
  creatable; 236/244 operators wired) and `VfxSelfTest.Run` (39 regression checks).

## Known limits

- No convert-to-subgraph for **blocks** yet — `tosubgraph` handles operators and parameters only.
- Groups and sticky notes are not copied by the clipboard.
- Experimental library entries need the VFX Graph "experimental" preference enabled before they can be used.
- A rolled-back `tosubgraph` leaves its new asset file on disk.

## What's inside

| Path | What |
|---|---|
| `Editor/Unity.Testing.VisualEffectGraph.Editor.asmdef` | the editor-only assembly (see *Assembly name* below) |
| `Editor/VfxGraphTool.cs` | `Describe`, `DescribeToFile`, `CompileReportToFile`, `ForceReimportToFile`, `FixOwnerOrder` |
| `Editor/VfxGraphOps.cs` | the op-script interpreter: `ApplyToFile(script, report)` / `Apply(script)` |
| `Editor/VfxClipboard.cs` | the model-level clipboard used by `clipcopy` / `clippaste` |
| `Editor/VfxCoverage.cs` | library coverage counter: `VfxCoverage.Run(outFile)` |
| `Editor/VfxSelfTest.cs` | regression self-test: `VfxSelfTest.Run(reportPath)` |
| `Skills~/vfx-graph/SKILL.md` | the skill that documents every call and op |
| `Samples~/SimpleLoopExample.vfxscript` | a worked op-script that builds a tunable effect from the `Simple_Loop` template |

All tool sources live in `Editor/`. See the repository README for the full guide.

## Requirements

- Unity **6000.6** (the package pins `"unity": "6000.6"`).
- VFX Graph **17.6.0** — declared as a dependency, so UPM resolves it automatically.
- The **`unity` CLI** (the `com.unity.pipeline` package) to drive it. The package's C# has no dependency on pipeline
  assemblies; the CLI is only the transport you call it through.

## Install

### 1. Git URL

Add to
`Packages/manifest.json`:

```json
"com.fenyce.vfxagent": "https://github.com/LucaBiada/vfxagent.git?path=/Packages/com.fenyce.vfxagent"
```

### 2. Local path (embedded)

From the Package Manager window: **Add package from disk…** and select this folder's `package.json`. Or in
`Packages/manifest.json`:

```json
"com.fenyce.vfxagent": "file:../../path/to/com.fenyce.vfxagent"
```

### 3. Tarball

```sh
# in this package folder
npm pack
# produces com.fenyce.vfxagent-0.1.0.tgz (rooted at package/)
```

Copy the `.tgz` into the project's `Packages/` folder and add:

```json
"com.fenyce.vfxagent": "file:com.fenyce.vfxagent-0.1.0.tgz"
```

Commit the `.tgz` with the project, or other clones cannot resolve it.

## Assembly name and the clash (read this before installing over the old copy)

The editor assembly is deliberately named **`Unity.Testing.VisualEffectGraph.Editor`**, because that name is on the
VFX Graph package's `InternalsVisibleTo` list (`com.unity.visualeffectgraph/Editor/PackageInfo.cs`, line 7), which is
what lets the tool use the package's internal editor API.

A Unity project can contain **only one** assembly with a given name. If an older copy of this tool exists under
`Assets/`, delete it first: only one assembly named `Unity.Testing.VisualEffectGraph.Editor` can exist in a project.
Installing this package while an old copy is present produces a duplicate-assembly compile error:

```
Multiple assemblies with equivalent name 'Unity.Testing.VisualEffectGraph.Editor' have been found.
```

**Detect the clash** from the CLI:

```
unity command eval "return UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(\"Unity.Testing.VisualEffectGraph.Editor\");"
```

- If the path is under `Packages/com.fenyce.vfxagent/` (or `Library/PackageCache/…`) you are fine.
- If it is under `Assets/`, that is the old copy.

> **A better-suited name exists.** `Unity.VisualEffectGraph.EditorTests` (the *first* entry on VFX's
> `InternalsVisibleTo` list) is also granted internals by VFX *Runtime*, Shader Graph *Editor* and HDRP *Runtime*, not
> just VFX Editor. This package keeps `Unity.Testing.VisualEffectGraph.Editor`, which only VFX Editor grants.

## Calling it from the `unity` CLI

All calls go through `unity command eval`. On PowerShell, write the C# to a file first and run `--file <path>`
(never inline), for example:

```powershell
# quick dump of a graph (no compile)
Set-Content -Path "$env:TEMP\agent\describe.cs" -Value 'return VfxGraphTool.Describe(@"Assets/FX/Sparks.vfx", false);'
unity command eval --caller plugin --skill unity-cli --format json --file "$env:TEMP\agent\describe.cs"
```

The three main entry points:

- `VfxGraphTool.Describe(path, compile:false)` — readable graph dump; run this first.
- `VfxGraphOps.ApplyToFile(scriptPath, reportPath)` — apply a whole op-script on the next editor tick, then poll for
  the report file (`VfxGraphOps.Apply(script)` runs synchronously when the report is not flushed).
- `VfxCoverage.Run(outFile)` — enumerate the whole VFX library into a fresh graph and write a coverage report.

For checks after a change, `VfxSelfTest.Run(reportPath)` replays the known-good op scripts and writes a pass/fail report.

The full call reference, the op-script grammar, and the workflow are in the skill: see
[`Skills~/vfx-graph/SKILL.md`](Skills~/vfx-graph/SKILL.md).

## The skill

The package ships a Claude Code / OpenCode skill at `Skills~/vfx-graph/SKILL.md` (name `vfx-graph`). Point the agent at
it; it is the source of truth for how to call the tool, the op list, and the known pitfalls. A ready example op-script
is in [`Samples~/SimpleLoopExample.vfxscript`](Samples~/SimpleLoopExample.vfxscript).

## Notes / open questions

- **Name.** `com.fenyce.vfxagent` and `Unity.Testing.VisualEffectGraph.Editor` are the package and assembly names. The
  assembly name is *borrowed from Unity's test internals grant* and is **not affiliated with Unity**.
- **Global namespace.** The three tool classes are `public static` in the **global namespace**.
- **Licensing** is deferred. This 0.x package is private/unofficial.
- **`.meta` files** are generated by Unity when the package is imported.
