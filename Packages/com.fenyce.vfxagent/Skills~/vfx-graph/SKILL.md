---
name: vfx-graph
description: Inspect, diagnose, fix and author Unity VFX Graph (.vfx) assets in this project through the VFX Graph package's own editor API (VfxGraphTool), driven from `unity command eval`. Use for any VFX Graph work - "particles don't show", compile problems, building or editing graphs. Never hand-edit .vfx YAML.
---

# VFX Graph via VfxGraphTool

`Packages/com.fenyce.vfxagent/Editor/` holds an editor-only assembly named **`Unity.Testing.VisualEffectGraph.Editor`**.
That name is in the VFX Graph package's `InternalsVisibleTo` list (`com.unity.visualeffectgraph/Editor/PackageInfo.cs`),
so `VfxGraphTool` can use the package's internal editor API (VFXGraph, VFXContext, VFXBlock, VFXSlot, VFXParameter,
compile, error reporter) - the same API the VFX Graph window uses. Code run directly by `unity command eval` can NOT
see those internals: put anything that needs them into `VfxGraphTool.cs`, recompile (`AssetDatabase.Refresh()` via
eval - `unity recompile` fails here), then call it.

Engine-internal types are not visible even to this assembly (VisualEffectResource, VisualEffectObject,
VFXCompilationMode, VisualEffectAssetDesc): reach them through reflection (see LoadGraph, CompileReportToFile).

## After changing the tool
Run `VfxSelfTest.Run(@"<report path>")` and require `35/35` (or the current total) before using the changed tool.
Run `VfxCoverage.Run` after library-related changes.

## Calls (from `unity command eval ... --result-only '<code>'`)
- `VfxGraphTool.Describe(path, compile:false)` - readable dump: contexts + flow links, blocks with settings/inputs/links,
  exposed parameters, operators, validation errors. Fast; use it first.
- `VfxGraphTool.DescribeToFile(path, outFile)` / `CompileReportToFile(path, outFile)` / `ForceReimportToFile(path, outFile)` -
  slow work (compiles take > 5 s, the CLI main-thread limit) runs on the next editor tick and writes a report file;
  poll with `EditorApplication.QueuePlayerLoopUpdate()` evals until the file exists. Use forward-slash paths (`cygpath -m`).
- `CompileReportToFile` compiles like the importer (Runtime mode) and lists the compiled systems: "success=True" with
  **0 systems** means every system was silently skipped (see Lessons).
- `FixOwnerOrder(path)` - reorders particle data owners (Init, Update, Output) and saves.
- `Save(graph)` (internal) - writes through `VisualEffectResourceExtensions.WriteAssetWithSubAssets`, like the window's Save.

## Editing: use the op-script interpreter (NO C# recompile per task)
`VfxGraphOps.ApplyToFile(@"<script>", "<report path>")` applies a whole list of edits in one go on the next editor
tick and writes a per-line report (stops at the first bad line, saying what exists). If the report never appears
(delayCall not flushing, seen 2026-11-08 with the editor "ready"), call `VfxGraphOps.Apply(script)` synchronously in the
eval instead: it may pass the 5 s limit but completes; check its returned report. The full op list is below; the
header of `Packages/com.fenyce.vfxagent/Editor/VfxGraphOps.cs` is the source of truth when the C# changes.
Workflow: Describe once -> write the full script
-> one ApplyToFile -> read report (compile line: systems > 0). Only touch the C# when an op is genuinely missing, and
then batch all C# changes into one recompile. In shell wrappers keep script and report file names distinct.
A failing line stops the script and ROLLS THE GRAPH BACK in memory: `Apply` keeps a `graph.Backup()` from after the
last `copy`/`open`/`newsubgraph`/`save` and on error calls `graph.Restore`, `PrepareGraph`, `UpdateSubAssets`,
`EditorUtility.SetDirty`. The report ends with `stopped; rolled back to the state before line N` (or `...after the
last save (line N)`), so a fixed script can be re-run without reopening the asset. Disk is untouched by the rollback:
a `save` line earlier in the script has already written those edits.
Recompile with `AssetDatabase.Refresh()` via eval, not `unity recompile` (fails here). Recompiles during VFX tool work
reset the user's layout and focused the Version Control tab; ordinary script recompiles do not (user, 2026-11-05) -
cause unknown, so keep VFX-tool C# changes rare and batched. Expose every tuning value as a parameter (`param` + `link`)
so the user tunes in the VisualEffect Inspector without any graph edit.
Custom HLSL lives in .hlsl files (e.g. Assets/VFX/MyEffect.hlsl) referenced by the block: **no apostrophes
anywhere in VFX HLSL, comments included** (generator breaks: "syntax error: unexpected token 's'", runtime
"Kernel at index (0) is invalid"). GPU shader errors are not in the compile report: check
`Logs/shadercompiler-*.log` for the effect name + "error".

## Op reference (one op per line, '#' comments, quote names with spaces)
Selectors: `Spawn` / `Init` / `Update` / `Output` (or `Output:<TypeName>`, e.g. `Output:VFXLitMeshOutput`);
`<Kind>@<name>` picks a context by its name or system title (e.g. `Spawn@spawn Circle U`); `#<id>` picks a
context made by `addctx`, `addsubgraph ctx` or `clippaste` earlier in the same script. Block selector = the block's name (substring ok);
`"name#n"` = the n-th block with that name (0-based).
- `copy <src.vfx> <dst.vfx>` - copy (replacing dst) and make dst the target.
- `open <path.vfx>` - target an existing graph.
- `newsubgraph <operator|block> <path>` - create and save an empty `.vfxoperator` / `.vfxblock` and make it the
  current target. The extension must match and the file must not exist; a block also gets its required
  `VFXBlockSubgraphContext`. e.g. `newsubgraph block Assets/VFX/SpawnShape.vfxblock`
- `subparam <in|out> <float|int|bool|vector3|mesh|texture2d|gradient|curve|skinnedmesh|transform> "<name>" [default]` -
  add an interface parameter in the current graph: `in` is exposed, `out` has `isOutput=true`.
  e.g. `subparam in float "Density" 0.5`  |  `subparam out vector3 "Result"`
  An `out` parameter has no output slot: link the internal producer into it with `link2 <src> P:<name>`.
- `enable <ctx> "<block>"` / `disable <ctx> "<block>"` - toggle a block on/off.
- `set <ctx> "<block>" <slot>=<value> ...` - block input values. Slot names may be dotted sub-slot paths:
  `arcSphere.sphere.center` (unnamed top slot: `.center`).
- `setctx <ctx> <slot>=<value> ...` - context input values (e.g. `bounds=aabox(0,6,0;50,24,50)`).
- `setop <OperatorType>[#n] <slot>=<value> ...` - input values of the first, or n-th, operator of that type.
- `setting <ctx> ["<block>"] <name>=<value> ...` - model SETTINGS (not slots); enums by name, e.g. `blendMode=Additive`.
- `param <float|int|bool|vector3|mesh|texture2d|gradient|curve|skinnedmesh|transform> "<name>" [value]` -
  create or update an exposed parameter.
- `paramrename "<old>" "<new>"` - rename an exposed parameter; errors if `<new>` already exists.
  e.g. `paramrename "Speed" "Velocity"`
- `paramdelete "<name>"` - delete an exposed parameter (unlinks and removes it).
  e.g. `paramdelete "Old Param"`
- `paramset "<name>" key=value ...` - set an exposed parameter's properties. Keys: `exposed=true|false`,
  `tooltip="text"`, `category="Cat"` (empty `""` = root), `order=<int>`, `value=<v>` (same parsing as `param`),
  `range=<min>;<max>` (float/int/uint only), `enum=A;B;C` (int/uint only), `filter=Default` (clear the filter).
  e.g. `paramset "Speed" range=0;100 tooltip="speed" category="Motion" order=1`
- `category add "<name>"` / `category rename "<old>" "<new>"` / `category delete "<name>"` - manage blackboard
  categories. `rename` updates the entry and every parameter whose category is `<old>`; `delete` removes only the
  entry and moves its parameters to the root (category = ""), it does NOT delete the parameters (unlike the window).
  e.g. `category add "Motion"`  |  `category rename "Motion" "Movement"`  |  `category delete "Motion"`
- `attr add "<name>" <float|int|uint|bool|vector2|vector3|vector4> ["description"]` - add a custom attribute.
  e.g. `attr add "MySeed" float "random seed"`
- `attr rename "<old>" "<new>"` - rename a custom attribute. e.g. `attr rename "MySeed" "Seed"`
- `attr delete "<name>"` - delete a custom attribute. e.g. `attr delete "MySeed"`
- `attr order "<name>" <int>` - reorder a custom attribute. e.g. `attr order "Seed" 0`
- `addblock <ctx> <BlockType> [setting=value ...]` - append a new block to the context.
  e.g. `addblock Init PositionMesh sourceMesh=SkinnedMeshRenderer`
- `addctx <ContextType> <id> [setting=value ...]` - new context; select it as `#<id>` in later lines.
  e.g. `addctx VFXLitPlanarPrimitiveOutput lit1 uvMode=Flipbook blendMode=Opaque`
- `linkflow <fromCtx> <toCtx>` - flow link (e.g. Update -> new Output).
  e.g. `linkflow Update #lit1`
- `unlinkflow <fromCtx> <toCtx>` - remove that one flow link (`toCtx.UnlinkFrom(fromCtx)`); errors if the contexts are
  not flow-linked. `removectx` already unlinks everything, so this is for rewiring a graph in place.
- `removectx <ctx>` - delete a context: unlinks its flow and data links first, then `VFXModel.RemoveModel` and a UI
  sanitize (so group contents drop it). e.g. `removectx Output`
- `opsetting <OperatorType[#n]> name=value ...` - SETTINGS of an existing operator; a value may be `asset:<path>`.
  e.g. `opsetting Random[0] constant=false`
- `addop <OperatorType> <id> [setting=value ...]` - new operator, referred to by `<id>` later in this script
  (types: `Random seed=PerParticle constant=true`, `Subtract`, `Divide`, `Saturate`, `OneMinus`, `Multiply`, ...).
  e.g. `addop Subtract sub1`. An unnamed output slot appears as `(unnamed)` in the report.
- `removeop <OperatorType[#n]|#id>` - delete an operator (first or n-th of a type, or an `addop` id as `#id`) with
  `VFXModel.RemoveModel` (unlinks all its data links) plus a UI sanitize, so it also leaves graph groups.
  e.g. `removeop Subtract`  |  `removeop Saturate#2`  |  `removeop #sub1`
- `clipcopy <item> [<item> ...]` - copy to a static clipboard that survives `open` and separate `Apply` calls.
  Items: `O:<id>` for an operator made earlier in this script (`addop`/`addsubgraph op`/`addvariant op`/`clippaste` id),
  `O:<OperatorType>[#n]` for an existing operator (same selector as `setop`, also accepted as `Op:<OperatorType>[#n]`),
  `P:<exposed name>` for a parameter, `C:<ctx selector>` for a context with its ordered blocks and data, or
  `B:<ctx>/<block>` for a block (the same block selector as `set`, including `name#n`). `B:` items must be copied alone,
  from one source context. Context copies include block settings/values/enabled state, data settings, and copied flow/data
  links. Groups and stickies are not supported; Custom HLSL blocks and subgraph references are copied as-is.
- `clippaste [<dx> <dy>]` - paste contexts/operators/parameters into the CURRENT graph at that offset (default `40 40`),
  e.g. after `open`ing another graph. Pasted operators are registered as `paste1`, `paste2`, ... and pasted contexts as
  `#pastectx1`, `#pastectx2`, ... (in paste order) for later ops. Cross-graph parameters are reused by name+type or created
  with a unique name; data links leaving the copied selection are dropped across graphs.
- `clippaste into <ctx selector>` - append a blocks-only clipboard to that context. Every block is checked with the
  target's `AcceptChild` compatibility rules; an incompatible block is an error, not silently skipped.
  Example: `clipcopy "B:Init/Set Position" B:Init/Velocity#0` then `clippaste into Update`.
  Cross-graph operator/parameter example: `open Assets/VFX/A.vfx` / `clipcopy Op:Multiply#1 P:Speed` /
  `open Assets/VFX/B.vfx` / `clippaste` / `link2 O:paste1 Op:sub1.a`
- `tosubgraph <new .vfxoperator path> <item> [<item> ...]` - convert a selection of operators and parameter nodes
  (`O:<id|Type[#n]>` / `P:<param>`, the `clipcopy` item forms; anything else, blocks/contexts included, is an error)
  into a NEW subgraph operator asset and replace them with one `VFXSubgraphOperator` node (script id `sub1`, then
  `sub2`, ...). Incoming links from nodes outside the selection become exposed INPUT parameters (one per distinct
  external source, named after the source parameter's exposed name or else the fed slot, made unique); outgoing links
  to nodes outside become `isOutput` OUTPUT parameters (named after the first outside input slot they feed, else the
  source slot, else `out`, made unique). Interface names contain no spaces or parentheses. A selected parameter still
  used outside stays in the graph and feeds the matching wrapper input. The new asset is saved before the wrapper
  node is built, then the originals are removed. The report lists the asset path, how many nodes moved, and the
  input/output names (use them with `link2`). Follow with `save` and usually `refreshparents <new asset>`. Limits:
  needs at least one operator; custom HLSL, groups and block conversion are not supported; a failure after the asset
  was created restores the current graph in memory but leaves the new asset file on disk (delete it or reuse the path).
  e.g. `open Assets/VFX/Main.vfx` / `tosubgraph Assets/VFX/Noise.vfxoperator O:Multiply#1 O:Random#0 P:Seed` /
  `save` / `refreshparents Assets/VFX/Noise.vfxoperator`
- `addsubgraph op <asset path> <id>` / `addsubgraph block <ctxSelector> <asset path>` /
  `addsubgraph ctx <asset path> <id>` - add a node referencing an existing `.vfxoperator`, `.vfxblock`, or `.vfx`
  asset respectively. `op` registers `<id>` for `O:` / `Op:` and `ctx` for `#<id>`; self/cyclic references are errors.
  e.g. `addsubgraph op Assets/VFX/Density.vfxoperator density1`  |
  `addsubgraph block Init Assets/VFX/SpawnShape.vfxblock`  |  `addsubgraph ctx Assets/VFX/Explosion.vfx explosion1`
- `addvariant <ctx|op|block> "<libraryName>" <id|ctx> [setting=value ...]` - new node from a VFXLibrary descriptor,
  picked by the EXACT name the VFX Graph node search shows (quote names with spaces or `|`; disambiguates entries
  that share a type: `Log/Log2/Log10`, `Normalize/Safe Normalize`, `Output Particle|...`). When descriptors share a
  name, a trailing `Name#k` (1-based, in library order) picks the k-th: in this project `"And#1"` = Logic And,
  `"And#2"` = Bitwise And (same for `Or`). ctx/op: 2nd arg = name,
  3rd = id; block: 2nd = ctx, 3rd = name. e.g. `addvariant op "Subtract" sub1`
- `link2 <src> <dst>` - link an output to an input. src: `P:<param>` | `O:<id>[.slot]`; dst: `Op:<id>.<slot>` |
  `B:<ctx>/<block>/<slot>` | `C:<ctx>/<slot>` | `P:<output param>` (quote specs with spaces). Subgraph operator slot
  names are its exposed input/output parameter names.
  `P:<name>` as a DESTINATION is the subgraph interface: a parameter with `isOutput == true` (made by
  `subparam out`) has no output slot, so an internal producer is linked into its `inputSlots[0]`; a non-output
  parameter is an error there (link into an actual input instead).
  e.g. `link2 P:Wind B:Init/Velocity/velocity`  |  `link2 O:density1.Result Op:mul1.a`  |
  `link2 O:mul1 P:Result` (feeds the subgraph's `out` interface parameter `Result`)
- `unlink <dst>` - remove the data link(s) on that input slot (`VFXSlot.UnlinkAll`); errors if nothing is linked.
  `unlink <src> <dst>` - remove only the link between that source (`P:`/`O:`, same syntax as `link2`) and that input
  (`VFXSlot.Unlink`); errors if they are not linked. Compound slots link per sub-slot
  (`arcSphere.sphere.center`), so pass the exact endpoint you linked. e.g. `unlink B:Init/Velocity/velocity`  |
  `unlink P:Wind B:Init/Velocity/velocity`
- `setin <dst> <value>` - set a value on an operator / block / context input slot (same dst syntax as `link2`).
  e.g. `setin Op:sub1.a 2.5`
- `space <ctx> "<block>" <slot> <None|Local|World>` - set a spaceable slot's space (e.g. a skinned mesh transform -> `World`).
- `link "<param>" <ctx> "<block>" <slot>` - link an exposed parameter to a block input.
- `linkop "<param>" <OperatorType> <slot>` - link an exposed parameter to an operator input.
- `linkctx "<param>" <ctx> <slot>` - link an exposed parameter to a context input (e.g. a mesh output's mesh).
- `hlsl <ctx> "<block name>" file=<path> function=<name>` - add or update a Custom HLSL block using a .hlsl file.
- `refreshparents <subgraph asset path>` - after saving the subgraph, find every root `.vfx` in `Assets/` whose
  recursive dependencies include it, then `PrepareGraph()` and force-reimport each one (logging every refreshed path).
  e.g. `refreshparents Assets/VFX/Density.vfxoperator`
- `save` / `compile` / `describe` - save; print a Runtime-mode compile report (check `systems=` > 0); print a
  Describe dump into the report. Subgraph nodes include their `m_Subgraph` asset path in the Describe dump.
Values: `0.3` `12` `true` `(0,-4,0)` `asset:Assets/x.png` `aabox(cx,cy,cz;sx,sy,sz)` `gradient:white-fade`
`curve:0=1;0.7=0.5;1=0` (AnimationCurve keys time=value, smoothed).

## Editing internals
- Copy a working graph as the starting point (`AssetDatabase.CopyAsset`), `LoadGraph` the copy, edit, `Save(graph)`,
  then `CompileReportToFile` to confirm systems > 0. Long edits: wrap in a `...ToFile` delayCall like the others.
- Toggle a block: `SetEnabled(block, bool)` (writes `block.activationSlot.value`; `VFXBlock.enabled` is read-only).
- Slot values: `Input(model, "name").value = ...`; links: `inputSlot.Link(outputSlot)`. `Input` lists the real slot
  names when a name is wrong.
- New exposed parameter: `AddExposedParameter<T>(graph, name, value)` (CreateInstance<VFXParameter> + Init(type)).
- Custom HLSL block: `CreateInstance<CustomHLSL>()`, `context.AddChild`, `sourceCode = code`, then
  `SelectHlslFunction(block, "Fn")` - it runs the FIRST function by default, so helpers defined above the main function
  get picked otherwise. Its input slots are the function's parameters prefixed with `_` (`_Radius`).
- Bounds: Initialize's `bounds` (AABox) is in system space; copying a graph made for a small object keeps tiny bounds.
- A Custom HLSL block added by script only gets attributes the system ALREADY stores: reading/writing a new one
  (seed via VFXRAND, particleId, a size/color that is a graph constant) fails in the GPU compile with
  "invalid subscript '<attr>'" while the compile report says success. Check `Logs/shadercompiler-*.log` for ok=0.
  Workaround: derive randomness from a stored random attribute (e.g. `asuint(attributes.lifetime)` hash).
- Sample Mesh / Position Mesh need the sampled mesh's FBX **Read/Write enabled**.

## Runtime checks (no internals needed)
`VisualEffect.GetParticleSystemNames/GetSpawnSystemNames` (+ `GetParticleSystemInfo(name).aliveCount`) on scene
components show what the imported asset really contains. `aliveCount` reads back from the GPU: 0 in Edit mode means nothing.

## Lessons
- A system compiles only if its `VFXDataParticle` owner list starts with the Init context and ends with an Output
  (`VFXDataParticle.CanBeCompiled`). Wrong order => silently skipped, no error anywhere.
- A disconnected Output gets its own empty particle data: particles simulate but nothing renders.
- Graph files reference meshes/textures by GUID inside slot values: a wrong mesh (e.g. triangle count from another
  model) silently kills spawning. Describe shows every slot value.
- The VFX editor keeps loaded graphs in memory: editing the .vfx on disk does nothing until the editor reloads it, and
  saving from the VFX window writes the in-memory version back. Always edit through the API, then Save.
- Every graph has a `388ad3b1...` sub-output with a missing script (URP sub-output; URP isn't installed). Harmless.
- Editor log for this project: `Logs/Editor.log`; VFX import lines say "[static dependencies only]" when nothing compiled.
- Lit HDRP outputs need `useNormalMap=true` for their normal slot to exist: set it (via `setting`/`addctx`)
  before trying to set or link the normal slot.
- An `asset:` value is a project path (`asset:Assets/x.png`), loaded through the AssetDatabase - not a GUID or name.
- Use `describe` to learn the real slot names; a wrong name lists the valid ones in the error.
- `addvariant` library names carry markup such as `|Set|_Position`; a wrong name returns the closest matching names.
- The CLI has a 5-second main-thread limit: long scripts must use `ApplyToFile` (write a report file) and poll
  for it - don't expect a long script's result inline.
