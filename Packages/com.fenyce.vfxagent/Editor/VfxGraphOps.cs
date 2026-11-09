// Text-driven VFX Graph editing: one call applies a whole list of edits (no C# recompile per task).
// Call from `unity command eval`:  return VfxGraphOps.ApplyToFile(@"<script>", "C:/.../report.txt");
// then poll for the report file (the work runs on the next editor tick: compiles take > 5 s).
// A failing line stops the script and rolls the graph back to the last checkpoint (after copy/open/newsubgraph/save),
// so partial edits never stay in memory.
//
// Script: one op per line, '#' comments. Quote names with spaces. Context selectors: Spawn, Init, Update, Output,
// Block selector "name#n" = n-th block with that name. <Kind>@<name> picks a context by its name or system title, e.g. "Spawn@spawn Circle U" (quote it when it has spaces),
// or Output:<TypeName> (e.g. Output:VFXLitMeshOutput). Block selector = substring of the block's name.
//   copy <src.vfx> <dst.vfx>                 copy (replacing dst) and make dst the target
//   open <path.vfx>                          target an existing graph
//   newsubgraph <operator|block> <path>      create an empty .vfxoperator/.vfxblock and make it the target (block gets its VFXBlockSubgraphContext)
//   subparam <in|out> <type> "<name>" [default]   add an exposed input or isOutput interface parameter in the current graph
//   enable  <ctx> "<block>"   /  disable <ctx> "<block>"
//   removeblock <ctx> "<block>"             remove a block from a context
//   set <ctx> "<block>" <slot>=<value> ...   block input values
//   setctx <ctx> <slot>=<value> ...          context input values (e.g. bounds=aabox(0,6,0;50,24,50))
//   setop <OperatorType>[#n] <slot>=<value> ...  operator input values (first, or n-th, operator of that type)
//   Slot names may be dotted sub-slot paths: arcSphere.sphere.center (unnamed top slot: .center)
//   setting <ctx> ["<block>"] <name>=<value> ...  model settings (not slots): enums by name, e.g. blendMode=Additive
//   param <float|int|bool|vector3|mesh|texture2d|gradient|curve|skinnedmesh|transform> "<name>" [value]   create or update an exposed parameter
//   addblock <ctx> <BlockType> [setting=value ...]   append a new block (e.g. PositionMesh sourceMesh=SkinnedMeshRenderer)
//   addctx <ContextType> <id> [setting=value ...]   new context, e.g. VFXLitPlanarPrimitiveOutput uvMode=Flipbook blendMode=Opaque; select it as #<id> in later lines
//   linkflow <fromCtx> <toCtx>   flow link (Update -> new Output); unlinkflow <fromCtx> <toCtx>   remove that flow link
//   removectx <ctx>   delete a context (unlinks its flow and data links first)
//   opsetting <OperatorType[#n]> name=value ...   settings of an existing operator (value may be asset:<path>)
//   addop <OperatorType> <id> [setting=value ...]   new operator (Random seed=PerParticle constant=true, Subtract, Divide, Saturate, OneMinus, Multiply, ...)
//   removeop <OperatorType[#n]|#id>   delete an operator (unlinks its data links, then cleans UI groups)
//   clipcopy O:<id|Type[#n]> | P:<param> | C:<ctx> | B:<ctx>/<block> ...   copy to a static clipboard (survives open and Apply calls)
//   clippaste [<dx> <dy>]   paste contexts/operators/parameters (default offset 40 40); operators become paste1, contexts #pastectx1, ...
//   clippaste into <ctx>    append a blocks-only clipboard (B: items) to a compatible target context
//   tosubgraph <path.vfxoperator> O:<id|Type[#n]> | P:<param> ...   move the selected operators/parameter nodes into a NEW
//            subgraph operator asset and replace them with one subgraph node (script id sub1); boundary links become its inputs/outputs
//   addsubgraph op <asset path> <id>   /   addsubgraph block <ctx> <asset path>   /   addsubgraph ctx <asset path> <id>   reference an existing subgraph asset
//   addvariant <ctx|op|block> "<libraryName>" <id|ctx> [setting=value ...]   new node from a VFXLibrary descriptor, picked by the exact name the VFX Graph node search shows (quote names with spaces or '|'; disambiguates entries that share a type: Log/Log2/Log10, Normalize/Safe Normalize, 'Output Particle|...'); a trailing 'Name#k' (1-based) picks the k-th descriptor with that name in the kind's list order (And#2 = 2nd 'And'). ctx/op: 2nd=name, 3rd=id; block: 2nd=ctx, 3rd=name.
//   link2 <src> <dst>        src: P:<param> | O:<id>[.slot]    dst: Op:<id>.<slot> | B:<ctx>/<block>/<slot> | C:<ctx>/<slot> | P:<output param>   (quote specs with spaces)
//   unlink <dst>             remove every data link into an input (same dst syntax); unlink <src> <dst> removes just that one link
//   setin <dst> <value>      set a value on an operator / block / context input slot (same dst syntax)
//   space <ctx> "<block>" <slot> <None|Local|World>   set a spaceable slot's space (e.g. a skinned mesh transform -> World)
//   link "<param>" <ctx> "<block>" <slot>   /  linkop "<param>" <OperatorType> <slot>   /  linkctx "<param>" <ctx> <slot>
//   hlsl <ctx> "<block name>" file=<path> function=<name>   add or update a Custom HLSL block using a .hlsl file
//   refreshparents <subgraph asset path>    PrepareGraph + force reimport every root .vfx in Assets that depends on the subgraph
//   save   |   compile   |   describe
// Values: 0.3  12  true  (0,-4,0)  asset:Assets/x.png  aabox(cx,cy,cz;sx,sy,sz)  gradient:white-fade
//         curve:0=1;0.7=0.5;1=0  (AnimationCurve keys time=value, smoothed)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.VFX;
using UnityEngine;
using UnityEngine.VFX;

public static class VfxGraphOps
{
    public static string ApplyToFile(string script, string outFile)
    {
        if (System.IO.File.Exists(outFile)) System.IO.File.Delete(outFile);
        EditorApplication.delayCall += () =>
        {
            string text;
            try { text = Apply(script); }
            catch (Exception e) { text = "FAILED: " + e; }
            System.IO.File.WriteAllText(outFile, text);
        };
        return "scheduled -> " + outFile;
    }

    /// Runs the script synchronously; stops at the first failing line and reports it.
    public static string Apply(string script)
    {
        var log = new StringBuilder();
        VFXGraph g = null; string path = null; bool dirty = false;
        object snapshot = null; string snapshotAt = null;   // g.Backup() at the last copy/open/newsubgraph/save (transaction rollback point)
        var ops = new Dictionary<string, IVFXSlotContainer>();   // operator ids made by addop/addsubgraph op in this script
        s_NewCtx.Clear();
        int lineNo = 0;
        foreach (var raw in script.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var t = Tokenize(line);
            try
            {
                switch (t[0])
                {
                    case "copy":
                        if (AssetDatabase.LoadMainAssetAtPath(t[2]) != null) AssetDatabase.DeleteAsset(t[2]);
                        if (!AssetDatabase.CopyAsset(t[1], t[2])) throw new Exception("copy failed");
                        path = t[2]; g = VfxGraphTool.LoadGraph(path);
                        snapshot = g.Backup(); snapshotAt = $"before line {lineNo + 1}";
                        log.AppendLine($"{lineNo}: copied -> {path}"); break;
                    case "open":
                        path = t[1]; g = VfxGraphTool.LoadGraph(path);
                        snapshot = g.Backup(); snapshotAt = $"before line {lineNo + 1}";
                        log.AppendLine($"{lineNo}: opened {path}"); break;
                    case "newsubgraph":   // newsubgraph <operator|block> <path>   create, save, and target an empty subgraph asset
                    {
                        if (t[1] != "operator" && t[1] != "block") throw new Exception("newsubgraph kind must be operator|block");
                        var ext = t[1] == "operator" ? ".vfxoperator" : ".vfxblock";
                        if (!t[2].EndsWith(ext, StringComparison.OrdinalIgnoreCase)) throw new Exception($"newsubgraph {t[1]} path must end with {ext}: {t[2]}");
                        if (System.IO.File.Exists(t[2])) throw new Exception("file already exists: " + t[2]);
                        if (t[1] == "operator") VisualEffectAssetEditorUtility.CreateNew<VisualEffectSubgraphOperator>(t[2]);
                        else VisualEffectAssetEditorUtility.CreateNew<VisualEffectSubgraphBlock>(t[2]);
                        path = t[2]; g = VfxGraphTool.LoadGraph(path);
                        if (t[1] == "block" && !g.children.OfType<VFXBlockSubgraphContext>().Any())
                            g.AddChild(ScriptableObject.CreateInstance<VFXBlockSubgraphContext>(), 0);
                        g.SetExpressionGraphDirty(); VfxGraphTool.Save(g); dirty = false;
                        snapshot = g.Backup(); snapshotAt = $"after the last save (line {lineNo})";
                        log.AppendLine($"{lineNo}: created {t[1]} subgraph {path}"); break;
                    }
                    case "enable":
                    case "disable":
                        VfxGraphTool.SetEnabled(Block(g, t[1], t[2]), t[0] == "enable"); dirty = true;
                        log.AppendLine($"{lineNo}: {t[0]}d {t[1]} '{t[2]}'"); break;
                    case "set":
                    {
                        var b = Block(g, t[1], t[2]);
                        foreach (var kv in t.Skip(3)) SetSlot(b, kv);
                        dirty = true; log.AppendLine($"{lineNo}: set {t[1]} '{b.name}' {string.Join(" ", t.Skip(3))}"); break;
                    }
                    case "setctx":
                    {
                        var c = Ctx(g, t[1]);
                        foreach (var kv in t.Skip(2)) SetSlot(c, kv);
                        dirty = true; log.AppendLine($"{lineNo}: setctx {t[1]} {string.Join(" ", t.Skip(2))}"); break;
                    }
                    case "setop":
                    {
                        var op = Op(g, t[1]);
                        foreach (var kv in t.Skip(2)) SetSlot(op, kv);
                        dirty = true; log.AppendLine($"{lineNo}: setop {t[1]} {string.Join(" ", t.Skip(2))}"); break;
                    }
                    case "setting":
                    {
                        bool onBlock = t.Count > 2 && !t[2].Contains("=");
                        VFXModel m = onBlock ? (VFXModel)Block(g, t[1], t[2]) : Ctx(g, t[1]);
                        foreach (var kv in t.Skip(onBlock ? 3 : 2)) ApplySetting(m, kv);
                        dirty = true; log.AppendLine($"{lineNo}: setting {string.Join(" ", t.Skip(1))}"); break;
                    }
                    case "addctx":   // addctx <ContextType> <id> [setting=value ...]   new context (e.g. VFXLitPlanarPrimitiveOutput); select it later as #<id>
                    {
                        var type = FindType(t[1], typeof(VFXContext)) ?? throw new Exception("no context type " + t[1]);
                        var c = (VFXContext)ScriptableObject.CreateInstance(type);
                        foreach (var kv in t.Skip(3)) ApplySetting(c, kv);
                        g.AddChild(c); s_NewCtx[t[2]] = c; dirty = true;
                        log.AppendLine($"{lineNo}: addctx {t[1]} #{t[2]} in=[{string.Join(", ", c.inputSlots.Select(x => x.property.name))}]"); break;
                    }
                    case "linkflow":   // linkflow <fromCtx> <toCtx>   flow link (Update -> Output etc.); contexts as usual selectors or #id
                    {
                        var from = Ctx(g, t[1]); var to = Ctx(g, t[2]);
                        from.LinkTo(to); dirty = true; log.AppendLine($"{lineNo}: linkflow {t[1]} -> {t[2]}"); break;
                    }
                    case "unlinkflow":   // unlinkflow <fromCtx> <toCtx>   remove that flow link
                    {
                        var from = Ctx(g, t[1]); var to = Ctx(g, t[2]);
                        if (!to.inputFlowSlot.Any(s => s.link.Any(l => l.context == from)))
                            throw new Exception($"{t[1]} is not flow-linked to {t[2]}");
                        to.UnlinkFrom(from); dirty = true;
                        log.AppendLine($"{lineNo}: unlinked flow {t[1]} -> {t[2]}"); break;
                    }
                    case "removeblock":
                    {
                        var b = Block(g, t[1], t[2]); var bn = b.name; ((VFXContext)b.GetParent()).RemoveChild(b); dirty = true; log.AppendLine($"{lineNo}: removed block '{bn}' from {t[1]}"); break;
                    }
                    case "removectx":   // removectx <ctx>   delete a context (flow links are pruned first, then model-level removal)
                    {
                        var c = Ctx(g, t[1]); c.UnlinkAll(); VFXModel.RemoveModel(c); g.UIInfos.Sanitize(g); dirty = true;
                        log.AppendLine($"{lineNo}: removed context {t[1]}"); break;
                    }
                    case "opsetting":   // opsetting <OperatorType[#n]> name=value ...   settings of an existing operator (asset:<path> allowed, e.g. a point cache)
                    {
                        var op = Op(g, t[1]) as VFXModel ?? throw new Exception("no operator " + t[1]);
                        foreach (var kv in t.Skip(2)) ApplySetting(op, kv);
                        dirty = true; log.AppendLine($"{lineNo}: opsetting {string.Join(" ", t.Skip(1))}"); break;
                    }
                    case "addop":   // addop <OperatorType> <id> [setting=value ...]   new operator, referred to by <id> later in this script
                    {
                        var type = FindType(t[1], typeof(VFXOperator)) ?? throw new Exception("no operator type " + t[1]);
                        var op = (VFXOperator)ScriptableObject.CreateInstance(type);
                        foreach (var kv in t.Skip(3)) ApplySetting(op, kv);
                        g.AddChild(op); op.ResyncSlots(true); ops[t[2]] = op; dirty = true;   // resync: outputs of numeric operators exist only after this
                        log.AppendLine($"{lineNo}: addop {t[1]} '{t[2]}' in=[{SlotNames(op.inputSlots)}] out=[{SlotNames(op.outputSlots)}]"); break;
                    }
                    case "removeop":   // removeop <OperatorType[#n]|#id>   delete an operator (unlinks its data links, then cleans UI groups)
                    {
                        var opId = t[1].StartsWith("#") ? t[1].Substring(1) : null;
                        var op = (opId != null ? OpById(ops, opId) : Op(g, t[1])) as VFXModel ?? throw new Exception("no operator " + t[1]);
                        if (!(op is VFXOperator)) throw new Exception($"{t[1]} is not an operator ({op.GetType().Name}); use removectx / paramdelete");
                        VFXModel.RemoveModel(op); g.UIInfos.Sanitize(g);   // Sanitize again: RemoveModel sanitizes before detaching
                        if (opId != null) ops.Remove(opId);                 // do not leave a detached operator reachable as O:/Op:
                        dirty = true; log.AppendLine($"{lineNo}: removed operator {t[1]}"); break;
                    }
                    case "clipcopy":   // clipcopy O:<id|Type[#n]> | P:<param> | C:<ctx> | B:<ctx>/<block> ...
                    {
                        if (t.Count < 2) throw new Exception("clipcopy needs at least one O:<id|Type[#n]>, P:<param>, C:<ctx> or B:<ctx>/<block> item");
                        var models = t.Skip(1).Select(item => ClipItem(g, ops, item)).ToList();
                        s_Clipboard = VfxClipboard.Copy(g, models);
                        log.AppendLine($"{lineNo}: clipcopied {s_Clipboard.nodes.Count} record(s){(s_Clipboard.blocksOnly ? " (blocks only)" : "")}: {string.Join(", ", models.Select(m => m is VFXParameter mp ? "P:" + mp.exposedName : m.name))}"); break;
                    }
                    case "clippaste":   // clippaste [<dx> <dy>] | clippaste into <ctx>
                    {
                        if (s_Clipboard == null) throw new Exception("the clipboard is empty; run clipcopy first");
                        if (t.Count >= 2 && t[1] == "into")
                        {
                            if (t.Count != 3) throw new Exception("clippaste into takes exactly one target context selector");
                            if (!s_Clipboard.blocksOnly) throw new Exception("clippaste into requires a blocks-only clipboard made with B:<ctx>/<block> items");
                            var targetContext = Ctx(g, t[2]);
                            dirty = true;   // set first: a failing paste must roll partially added blocks back too
                            var blocks = VfxClipboard.PasteBlocks(targetContext, s_Clipboard);
                            log.AppendLine($"{lineNo}: clippasted {blocks.Count} block(s) into {t[2]}: {string.Join(", ", blocks.Select(b => b.name))}"); break;
                        }
                        if (t.Count != 1 && t.Count != 3) throw new Exception("clippaste takes no arguments, <dx> <dy>, or into <ctx>");
                        if (s_Clipboard.blocksOnly) throw new Exception("a blocks-only clipboard requires clippaste into <ctx>");
                        var offset = t.Count == 3 ? new Vector2(float.Parse(t[1], Inv), float.Parse(t[2], Inv)) : new Vector2(40f, 40f);
                        dirty = true;   // set first: a failing paste must roll the partially added nodes back too
                        var pasted = VfxClipboard.Paste(g, s_Clipboard, offset);
                        var names = new List<string>(); int k = 0, contextK = 0;
                        foreach (var m in pasted)
                        {
                            if (m is VFXOperator op) { ops["paste" + ++k] = op; names.Add($"paste{k}={op.GetType().Name}"); }
                            else if (m is VFXParameter p) names.Add($"P:{p.exposedName}");
                            else if (m is VFXContext c)
                            {
                                string id = "pastectx" + ++contextK;
                                s_NewCtx[id] = c;
                                names.Add($"#{id}={c.GetType().Name}({c.children.Count()} blocks)");
                            }
                        }
                        log.AppendLine($"{lineNo}: clippasted {pasted.Count} top-level node(s) at ({offset.x}, {offset.y}): {string.Join(", ", names)}"); break;
                    }
                    case "tosubgraph":   // tosubgraph <path.vfxoperator> <item> [<item> ...]   convert selected operators/parameter nodes to a subgraph operator
                    {
                        if (t.Count < 3) throw new Exception("tosubgraph needs <path.vfxoperator> and at least one O:<id|Type[#n]> or P:<param> item");
                        var assetPath = t[1];
                        if (!assetPath.EndsWith(".vfxoperator", StringComparison.OrdinalIgnoreCase)) throw new Exception("tosubgraph path must end with .vfxoperator: " + assetPath);
                        if (System.IO.File.Exists(assetPath)) throw new Exception("file already exists: " + assetPath);
                        var models = t.Skip(2).Select(x => ClipItem(g, ops, x)).Distinct().ToList();
                        if (models.Any(m => !(m is VFXOperator) && !(m is VFXParameter)))
                            throw new Exception("tosubgraph items must be O:<id|Type[#n]> (operator) or P:<param>: " + string.Join(" ", t.Skip(2)));
                        if (!models.OfType<VFXOperator>().Any()) throw new Exception("tosubgraph needs at least one operator in the selection");

                        // Boundary analysis BEFORE any mutation. Edges to/from a selected parameter are copied with it and,
                        // if it still feeds something outside, the original parameter stays and feeds the new wrapper input.
                        var sel = new HashSet<VFXModel>(models);
                        var incoming = new List<(VFXSlot src, VFXSlot dst)>();
                        var outgoing = new List<(VFXSlot src, VFXSlot dst)>();
                        foreach (var m in models)
                        {
                            var c = (IVFXSlotContainer)m;
                            // stock's recursive walk: boundary links may sit on nested sub-slots (e.g. arcSphere.sphere.center)
                            foreach (var i in VfxClipboard.Stock.Slots(c.inputSlots))
                                foreach (var s in i.LinkedSlots.ToArray())
                                    if (!(s.owner is VFXModel so && sel.Contains(so))) incoming.Add((s, i));
                            foreach (var o in VfxClipboard.Stock.Slots(c.outputSlots))
                                foreach (var d in o.LinkedSlots.ToArray())
                                    if (!(d.owner is VFXModel ds && sel.Contains(ds))) outgoing.Add((o, d));
                        }
                        outgoing.RemoveAll(e => e.src.owner is VFXParameter);

                        // 1) create the child asset and paste the selection into it (the clipboard keeps the internal links).
                        // NOTE: if a later step throws, the interpreter restores the CURRENT graph, but this asset file stays on disk.
                        var clip = VfxClipboard.Copy(g, models);   // validates the selection before the asset file exists
                        var asset = VisualEffectAssetEditorUtility.CreateNew<VisualEffectSubgraphOperator>(assetPath) ?? throw new Exception("could not create " + assetPath);
                        var child = VfxGraphTool.LoadGraph(assetPath);
                        var pasted = VfxClipboard.Paste(child, clip, Vector2.zero);
                        if (pasted.Count != models.Count) throw new Exception($"pasted {pasted.Count} of {models.Count} node(s) into {assetPath}");
                        var clone = models.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => (VFXModel)pasted[x.i]);
                        foreach (var p in models.OfType<VFXParameter>())
                            if (clone[p] is VFXParameter cp && !cp.isOutput)
                                cp.SetSettingValue("m_Exposed", true);   // stock conversion: every moved input parameter is exposed

                        // 2) interface: one input per distinct external source, one output per distinct moved output
                        var inputs = new List<(string name, VFXSlot src)>();
                        foreach (var grp in incoming.GroupBy(e => e.src))
                        {
                            var dst = grp.First().dst;
                            // external source: name the input after the exposed parameter it comes from, else after the fed slot
                            string wanted = grp.Key.owner is VFXParameter sp && !string.IsNullOrEmpty(sp.exposedName) ? sp.exposedName : InterfaceName(dst);
                            var p = NewInterfaceParam(child, grp.Key.property.type, grp.Key, wanted, false);
                            if (!p.outputSlots[0].Link(CloneSlot(clone, dst, true))) throw new Exception($"cannot link interface input '{p.exposedName}' to the copied '{dst.property.name}'");
                            inputs.Add((p.exposedName, grp.Key));
                        }
                        var outputs = new List<(string name, VFXSlot src, VFXSlot[] dsts)>();
                        foreach (var grp in outgoing.GroupBy(e => e.src))
                        {
                            // name the output after the first outside input slot it feeds, else the source slot, else "out"
                            string wanted = grp.First().dst.property.name;
                            if (string.IsNullOrEmpty(wanted)) wanted = grp.Key.property.name;
                            if (string.IsNullOrEmpty(wanted)) wanted = "out";
                            var p = NewInterfaceParam(child, grp.Key.property.type, grp.Key, wanted, true);
                            if (!p.inputSlots[0].Link(CloneSlot(clone, grp.Key, false))) throw new Exception($"cannot link the copied '{grp.Key.property.name}' to interface output '{p.exposedName}'");
                            outputs.Add((p.exposedName, grp.Key, grp.Select(e => e.dst).ToArray()));
                        }
                        child.SetExpressionGraphDirty(); VfxGraphTool.Save(child);   // mandatory: wrapper slots are built from the saved asset

                        // 3) replacement node in the current graph (same calls as 'addsubgraph op')
                        var wrapper = ScriptableObject.CreateInstance<VFXSubgraphOperator>();
                        dirty = true;   // set first: a failing resync/rewire must roll the partially added node back too
                        g.AddChild(wrapper); wrapper.SetSettingValue("m_Subgraph", asset); wrapper.RecreateCopy(); wrapper.ResyncSlots(true);
                        wrapper.position = new Vector2(models.Average(m => m.position.x), models.Average(m => m.position.y));
                        int n = 1; while (ops.ContainsKey("sub" + n)) n++;
                        string id = "sub" + n; ops[id] = wrapper;

                        // 4) re-wire (Link replaces the old link on a target input), then remove the moved originals
                        var keepParams = models.OfType<VFXParameter>()
                            .Where(p => p.outputSlots.Count > 0 && p.outputSlots[0].LinkedSlots.Any(d => !(d.owner is VFXModel o) || !sel.Contains(o))).ToList();
                        foreach (var (name, src) in inputs)
                            if (!InterfaceSlot(wrapper.inputSlots, name).Link(src)) throw new Exception($"cannot link '{name}' <- {src.property.name} on the new subgraph node");
                        foreach (var p in keepParams)
                            if (clone[p] is VFXParameter cp)
                            {
                                var slot = wrapper.inputSlots.FirstOrDefault(x => x.property.name == cp.exposedName);
                                if (slot != null && !slot.HasLink()) slot.Link(p.outputSlots[0]);
                            }
                        foreach (var o in outputs) foreach (var dst in o.dsts)
                            if (!InterfaceSlot(wrapper.outputSlots, o.name).Link(dst)) throw new Exception($"cannot link '{o.name}' -> {dst.property.name} on the new subgraph node");
                        foreach (var m in models)
                            if (!(m is VFXParameter) || !keepParams.Contains(m)) VFXModel.RemoveModel(m);
                        g.UIInfos.Sanitize(g);
                        log.AppendLine($"{lineNo}: tosubgraph {assetPath} as O:{id}: moved {models.Count} node(s); inputs=[{string.Join(", ", inputs.Select(x => x.name))}] outputs=[{string.Join(", ", outputs.Select(x => x.name))}]"); break;
                    }
                    case "addsubgraph":   // addsubgraph op <asset path> <id> | block <ctx> <asset path> | ctx <asset path> <id>
                    {
                        switch (t[1])
                        {
                            case "op":
                            {
                                var asset = AssetDatabase.LoadAssetAtPath<VisualEffectSubgraphOperator>(t[2]) ?? throw new Exception("no subgraph operator asset at " + t[2]);
                                CheckedSubgraph(g, path, t[2]);
                                var op = ScriptableObject.CreateInstance<VFXSubgraphOperator>();
                                g.AddChild(op); op.SetSettingValue("m_Subgraph", asset); op.ResyncSlots(true); ops[t[3]] = op; dirty = true;
                                log.AppendLine($"{lineNo}: addsubgraph op '{t[2]}' '{t[3]}' in=[{string.Join(", ", op.inputSlots.Select(x => x.property.name))}] out=[{string.Join(", ", op.outputSlots.Select(x => x.property.name))}]"); break;
                            }
                            case "block":
                            {
                                var c = Ctx(g, t[2]);
                                var asset = AssetDatabase.LoadAssetAtPath<VisualEffectSubgraphBlock>(t[3]) ?? throw new Exception("no subgraph block asset at " + t[3]);
                                var subGraph = CheckedSubgraph(g, path, t[3]);
                                var subCtx = subGraph.children.OfType<VFXBlockSubgraphContext>().FirstOrDefault() ?? throw new Exception("block subgraph has no VFXBlockSubgraphContext: " + t[3]);
                                if (!c.Accept(subCtx.compatibleContextType, subCtx.ownedType)) throw new Exception($"subgraph block '{t[3]}' is not compatible with context '{t[2]}'");
                                var b = ScriptableObject.CreateInstance<VFXSubgraphBlock>();
                                c.AddChild(b); b.SetSettingValue("m_Subgraph", asset); b.ResyncSlots(true); dirty = true;
                                log.AppendLine($"{lineNo}: addsubgraph block '{t[3]}' -> {t[2]} in=[{string.Join(", ", b.inputSlots.Select(x => x.property.name))}]"); break;
                            }
                            case "ctx":
                            {
                                var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(t[2]) ?? throw new Exception("no VisualEffectAsset at " + t[2]);
                                CheckedSubgraph(g, path, t[2]);
                                var c = ScriptableObject.CreateInstance<VFXSubgraphContext>();
                                g.AddChild(c); c.SetSettingValue("m_Subgraph", asset); c.ResyncSlots(true); s_NewCtx[t[3]] = c; dirty = true;
                                log.AppendLine($"{lineNo}: addsubgraph ctx '{t[2]}' #{t[3]} in=[{string.Join(", ", c.inputSlots.Select(x => x.property.name))}]"); break;
                            }
                            default: throw new Exception("addsubgraph kind must be op|block|ctx");
                        }
                        break;
                    }
                    case "addvariant":   // addvariant <ctx|op|block> "<libraryName>" <id|ctx> [setting=value ...]   new node from a VFXLibrary descriptor, by the exact name the VFX Graph node search shows (quote names with spaces or '|'; a trailing 'Name#k', 1-based, picks the k-th descriptor with that name)
                    {
                        var m = PickVariant(t[1], t[1] == "block" ? t[3] : t[2]);
                        foreach (var kv in t.Skip(4)) ApplySetting(m, kv);
                        switch (t[1])
                        {
                            case "ctx":   var c = (VFXContext)m;  g.AddChild(c); s_NewCtx[t[3]] = c; break;
                            case "op":    var o = (VFXOperator)m; g.AddChild(o); o.ResyncSlots(true); ops[t[3]] = o; break;
                            default:      Ctx(g, t[2]).AddChild((VFXBlock)m); break;   // block: t[2] is the ctx selector
                        }
                        dirty = true; log.AppendLine($"{lineNo}: addvariant {t[1]} '{(t[1] == "block" ? t[3] : t[2])}' -> {(t[1] == "block" ? t[2] : "#" + t[3])}"); break;
                    }
                    case "link2":   // link2 <src> <dst>   src: P:<param> | O:<id>[.slot]   dst: Op:<id>.<slot> | B:<ctx>/<block>/<slot> | C:<ctx>/<slot> | P:<output param>
                    {
                        var src = OutSlot(g, ops, t[1]); var dst = InSlot(g, ops, t[2]);
                        if (!dst.CanLink(src)) throw new Exception($"cannot link {t[1]} ({src.property.type.Name}) -> {t[2]} ({dst.property.type.Name})");
                        dst.Link(src); dirty = true; log.AppendLine($"{lineNo}: linked {t[1]} -> {t[2]}"); break;
                    }
                    case "unlink":   // unlink <dst>   every data link into that input; unlink <src> <dst>   only that one link
                    {
                        if (t.Count != 2 && t.Count != 3) throw new Exception("unlink takes <dst> or <src> <dst>");
                        var dst = InSlot(g, ops, t[t.Count - 1]);
                        if (t.Count == 2)
                        {
                            if (!dst.HasLink()) throw new Exception($"nothing is linked into {t[1]}");
                            dst.UnlinkAll(); dirty = true; log.AppendLine($"{lineNo}: unlinked all {t[1]}"); break;
                        }
                        var src = OutSlot(g, ops, t[1]);
                        if (!dst.LinkedSlots.Contains(src)) throw new Exception($"{t[1]} is not linked to {t[2]}");
                        dst.Unlink(src); dirty = true; log.AppendLine($"{lineNo}: unlinked {t[1]} -> {t[2]}"); break;
                    }
                    case "setin":   // setin <dst> <value>   value on an operator / block / context input slot
                    {
                        var dst = InSlot(g, ops, t[1]); dst.value = ParseValue(t[2], dst.property.type);
                        dirty = true; log.AppendLine($"{lineNo}: setin {t[1]} = {t[2]}"); break;
                    }
                    case "space":   // space <ctx> "<block>" <slot> <None|Local|World>   space of a spaceable slot (position, transform...)
                    {
                        var s = VfxGraphTool.Input(Block(g, t[1], t[2]), t[3]);
                        s.space = (UnityEngine.VFX.VFXSpace)Enum.Parse(typeof(UnityEngine.VFX.VFXSpace), t[4]);
                        dirty = true; log.AppendLine($"{lineNo}: space {t[1]} '{t[2]}'.{t[3]} = {s.space}"); break;
                    }
                    case "addblock":   // addblock <ctx> <BlockType> [setting=value ...]   appended at the end of the context
                    {
                        var c = Ctx(g, t[1]);
                        var type = FindType(t[2], typeof(VFXBlock)) ?? throw new Exception("no block type " + t[2]);
                        var b = (VFXBlock)ScriptableObject.CreateInstance(type);
                        foreach (var kv in t.Skip(3)) ApplySetting(b, kv);   // settings first: they decide the block's slots
                        c.AddChild(b);
                        dirty = true; log.AppendLine($"{lineNo}: added block '{b.name}' to {t[1]}"); break;
                    }
                    case "param":
                    {
                        var p = g.children.OfType<VFXParameter>().FirstOrDefault(x => x.exposedName == t[2]);
                        var type = ParamType(t[1]);
                        if (p == null)
                        {
                            p = ScriptableObject.CreateInstance<VFXParameter>();
                            p.Init(type); g.AddChild(p);
                            p.SetSettingValue("m_ExposedName", t[2]); p.SetSettingValue("m_Exposed", true);
                        }
                        if (t.Count > 3) p.value = ParseValue(t[3], type);
                        dirty = true; log.AppendLine($"{lineNo}: param {t[1]} '{t[2]}' = {(t.Count > 3 ? t[3] : "(default)")}"); break;
                    }
                    case "subparam":   // subparam <in|out> <type> "<name>" [default]   interface parameter in the current graph
                    {
                        if (t[1] != "in" && t[1] != "out") throw new Exception("subparam direction must be in|out");
                        var p = g.children.OfType<VFXParameter>().FirstOrDefault(x => x.exposedName == t[3]);
                        var type = ParamType(t[2]);
                        if (p == null)
                        {
                            p = ScriptableObject.CreateInstance<VFXParameter>();
                            p.Init(type); g.AddChild(p);
                            p.SetSettingValue("m_ExposedName", t[3]);
                        }
                        p.isOutput = t[1] == "out";
                        if (t[1] == "in") p.SetSettingValue("m_Exposed", true);
                        if (t.Count > 4)
                        {
                            var value = ParseValue(t[4], type);
                            if (p.isOutput) p.inputSlots[0].value = value;
                            else p.value = value;
                        }
                        dirty = true; log.AppendLine($"{lineNo}: subparam {t[1]} {t[2]} '{t[3]}' = {(t.Count > 4 ? t[4] : "(default)")}"); break;
                    }
                    case "paramrename":   // paramrename "<old>" "<new>"   rename an exposed parameter (errors if <new> exists)
                    {
                        var p = Param(g, t[1]);
                        if (g.children.OfType<VFXParameter>().Any(x => x != p && x.exposedName == t[2]))
                            throw new Exception($"parameter '{t[2]}' already exists");
                        p.SetSettingValue("m_ExposedName", t[2]);
                        dirty = true; log.AppendLine($"{lineNo}: paramrename '{t[1]}' -> '{t[2]}'"); break;
                    }
                    case "paramdelete":   // paramdelete "<name>"   delete an exposed parameter (unlinks + removes)
                    {
                        var p = Param(g, t[1]);
                        VFXModel.RemoveModel(p);
                        dirty = true; log.AppendLine($"{lineNo}: paramdelete '{t[1]}'"); break;
                    }
                    case "paramset":   // paramset "<name>" key=value ...   exposed/tooltip/category/order/value/range/enum/filter
                    {
                        var p = Param(g, t[1]);
                        var type = p.type;
                        foreach (var kv in t.Skip(2))
                        {
                            int eq = kv.IndexOf('=');
                            if (eq < 0) throw new Exception("paramset arg must be key=value: '" + kv + "'");
                            var key = kv.Substring(0, eq);
                            var val = Unquote(kv.Substring(eq + 1));
                            switch (key)
                            {
                                case "exposed": p.SetSettingValue("m_Exposed", bool.Parse(val)); break;
                                case "tooltip": p.tooltip = val; break;
                                case "category": p.category = val; break;
                                case "order": p.order = int.Parse(val, Inv); break;
                                case "value": p.value = ParseValue(val, type); break;
                                case "range":
                                {
                                    if (!p.canHaveValueFilter) throw new Exception($"range filter needs a float/int/uint parameter (got {type?.Name})");
                                    var mm = val.Split(';');
                                    p.valueFilter = VFXValueFilter.Range;
                                    p.min = ParseValue(mm[0], type); p.max = ParseValue(mm[1], type);
                                    break;
                                }
                                case "enum":
                                    if (type != typeof(int) && type != typeof(uint)) throw new Exception($"enum filter needs an int/uint parameter (got {type?.Name})");
                                    p.valueFilter = VFXValueFilter.Enum;
                                    p.enumValues = val.Split(';').ToList();
                                    break;
                                case "filter":
                                    if (val != "Default") throw new Exception("filter only supports 'Default'");
                                    p.valueFilter = VFXValueFilter.Default;
                                    break;
                                default: throw new Exception("unknown paramset key '" + key + "'");
                            }
                        }
                        dirty = true; log.AppendLine($"{lineNo}: paramset '{t[1]}' {string.Join(" ", t.Skip(2))}"); break;
                    }
                    case "category":   // category add|rename|delete "<name>" [<new>]   blackboard categories
                    {
                        var cats = g.UIInfos.categories;
                        switch (t[1])
                        {
                            case "add":
                                if (cats.Any(c => c.name == t[2])) throw new Exception($"category '{t[2]}' already exists");
                                cats.Add(new VFXUI.CategoryInfo { name = t[2] });
                                g.Invalidate(VFXModel.InvalidationCause.kUIChanged);
                                dirty = true; log.AppendLine($"{lineNo}: category add '{t[2]}'"); break;
                            case "rename":
                            {
                                int i = cats.FindIndex(c => c.name == t[2]);
                                if (i < 0) throw new Exception($"no category '{t[2]}'");
                                var c = cats[i]; c.name = t[3]; cats[i] = c;
                                foreach (var q in g.children.OfType<VFXParameter>().Where(q => q.category == t[2])) q.category = t[3];
                                g.Invalidate(VFXModel.InvalidationCause.kUIChanged);
                                dirty = true; log.AppendLine($"{lineNo}: category rename '{t[2]}' -> '{t[3]}'"); break;
                            }
                            case "delete":
                            {
                                int i = cats.FindIndex(c => c.name == t[2]);
                                if (i < 0) throw new Exception($"no category '{t[2]}'");
                                cats.RemoveAt(i);
                                foreach (var q in g.children.OfType<VFXParameter>().Where(q => q.category == t[2])) q.category = "";
                                g.Invalidate(VFXModel.InvalidationCause.kUIChanged);
                                dirty = true; log.AppendLine($"{lineNo}: category delete '{t[2]}'"); break;
                            }
                            default: throw new Exception("category subcommand must be add|rename|delete");
                        }
                        break;
                    }
                    case "attr":   // attr add|rename|delete|order   custom attributes
                        switch (t[1])
                        {
                            case "add":
                            {
                                if (!g.TryAddCustomAttribute(t[2], AttrValueType(t[3]), t.Count > 4 ? t[4] : "", false, out _))
                                    throw new Exception($"custom attribute '{t[2]}' already exists or could not be added");
                                dirty = true; log.AppendLine($"{lineNo}: attr add '{t[2]}' {t[3]}{(t.Count > 4 ? " '" + t[4] + "'" : "")}"); break;
                            }
                            case "rename":
                                if (!g.TryRenameCustomAttribute(t[2], t[3]))
                                    throw new Exception($"could not rename custom attribute '{t[2]}' -> '{t[3]}'");
                                dirty = true; log.AppendLine($"{lineNo}: attr rename '{t[2]}' -> '{t[3]}'"); break;
                            case "delete":
                                g.RemoveCustomAttribute(t[2]);
                                dirty = true; log.AppendLine($"{lineNo}: attr delete '{t[2]}'"); break;
                            case "order":
                                g.SetCustomAttributeOrder(t[2], int.Parse(t[3], Inv));
                                dirty = true; log.AppendLine($"{lineNo}: attr order '{t[2]}' = {t[3]}"); break;
                            default: throw new Exception("attr subcommand must be add|rename|delete|order");
                        }
                        break;
                    case "link":
                        VfxGraphTool.Input(Block(g, t[2], t[3]), t[4]).Link(Param(g, t[1]).outputSlots[0]);
                        dirty = true; log.AppendLine($"{lineNo}: linked '{t[1]}' -> {t[2]} '{t[3]}'.{t[4]}"); break;
                    case "linkctx":   // linkctx "<param>" <ctx> <slot>   (context inputs, e.g. a mesh output's mesh)
                        VfxGraphTool.Input(Ctx(g, t[2]), t[3]).Link(Param(g, t[1]).outputSlots[0]);
                        dirty = true; log.AppendLine($"{lineNo}: linked '{t[1]}' -> {t[2]}.{t[3]}"); break;
                    case "linkop":
                        VfxGraphTool.Input(Op(g, t[2]), t[3]).Link(Param(g, t[1]).outputSlots[0]);
                        dirty = true; log.AppendLine($"{lineNo}: linked '{t[1]}' -> {t[2]}.{t[3]}"); break;
                    case "hlsl":
                        Hlsl(g, t, log, lineNo); dirty = true; break;
                    case "refreshparents":   // refreshparents <subgraph asset path>   prepare + force reimport dependent root .vfx assets in Assets
                    {
                        if (AssetDatabase.LoadMainAssetAtPath(t[1]) == null) throw new Exception("no subgraph asset at " + t[1]);
                        var parents = AssetDatabase.FindAssets("t:VisualEffectAsset").Select(AssetDatabase.GUIDToAssetPath)
                            .Where(p => p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
                            .Where(p => !string.Equals(p, t[1], StringComparison.OrdinalIgnoreCase))
                            .Where(p => AssetDatabase.GetDependencies(p, true).Any(d => string.Equals(d, t[1], StringComparison.OrdinalIgnoreCase)))
                            .Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                        foreach (var parentPath in parents)
                        {
                            var parent = VfxGraphTool.LoadGraph(parentPath);
                            parent.PrepareGraph();
                            VFXGraph.UnregisterAuthoringCompileData(AssetDatabase.GUIDFromAssetPath(parentPath));
                            AssetDatabase.ImportAsset(parentPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                            log.AppendLine($"{lineNo}: refreshed {parentPath}");
                        }
                        if (parents.Count == 0) log.AppendLine($"{lineNo}: no root .vfx parents reference {t[1]}");
                        break;
                    }
                    case "save":
                        g.SetExpressionGraphDirty(); VfxGraphTool.Save(g); dirty = false;
                        snapshot = g.Backup(); snapshotAt = $"after the last save (line {lineNo})";
                        log.AppendLine($"{lineNo}: saved {path}"); break;
                    case "compile":
                        log.AppendLine(CompileReport(g)); break;
                    case "describe":
                        log.AppendLine(VfxGraphTool.Describe(path, false)); break;
                    default: throw new Exception("unknown op '" + t[0] + "'");
                }
            }
            catch (Exception e)
            {
                log.AppendLine($"{lineNo}: ERROR in '{line}': {(e is TargetInvocationException ti ? ti.InnerException : e).Message}");
                if (dirty && g != null && snapshot != null)
                {
                    g.Restore(snapshot);
                    g.PrepareGraph(); g.UpdateSubAssets(); EditorUtility.SetDirty(g);
                    log.AppendLine($"stopped; rolled back to the state {snapshotAt}");
                }
                else log.AppendLine("stopped");
                return log.ToString();
            }
        }
        return log.ToString();
    }

    // type by name among ALL loaded assemblies (HDRP outputs live in the HDRP editor assembly, not in the VFX package)
    static Type FindType(string name, Type baseType)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var x in types) if (x.Name == name && baseType.IsAssignableFrom(x) && !x.IsAbstract) return x;
        }
        return null;
    }

    // Find a VFXLibrary descriptor by its exact node-search name (d.name) and build its model (the descriptor's variant
    // settings are applied by CreateInstance). A trailing '#<k>' (1-based) picks the k-th descriptor named exactly this
    // in the kind's list order ('And#2' = 2nd 'And'); no suffix = the first (unchanged). No library name contains a
    // literal '#', so only a trailing '#<digits>' is stripped. NOTE: experimental entries are hidden by
    // GetContexts()/GetOperators()/GetBlocks() unless VFXViewPreference.displayExperimentalOperator is on, so addvariant
    // can't reach them unless that view option is set.
    static VFXModel PickVariant(string kind, string libraryName)
    {
        VFXLibrary.Load();
        IEnumerable<IVFXModelDescriptor> descs = kind switch
        {
            "ctx"   => (IEnumerable<IVFXModelDescriptor>)VFXLibrary.GetContexts(),
            "op"    => (IEnumerable<IVFXModelDescriptor>)VFXLibrary.GetOperators(),
            "block" => (IEnumerable<IVFXModelDescriptor>)VFXLibrary.GetBlocks(),
            _       => throw new Exception("addvariant kind must be ctx|op|block")
        };
        var all = descs.ToList();
        int k = 1; int hash = libraryName.LastIndexOf('#');
        if (hash > 0 && int.TryParse(libraryName.Substring(hash + 1), out var idx)) { k = idx; libraryName = libraryName.Substring(0, hash); }
        var same = all.Where(x => x.name == libraryName).ToList();
        if (same.Count == 0)
            throw new Exception($"no {kind} named '{libraryName}' in the VFX library; closest: [{string.Join(" | ", all.Select(x => x.name).Where(n => n.Contains(libraryName, StringComparison.OrdinalIgnoreCase) || libraryName.Contains(n, StringComparison.OrdinalIgnoreCase)).Distinct().Take(10))}]");
        if (k < 1 || k > same.Count)
            throw new Exception($"no {kind} '{libraryName}#{k}': {same.Count} descriptor(s) share the name '{libraryName}': [{string.Join(", ", same.Select(x => x.modelType.Name))}]");
        return same[k - 1].CreateInstance();
    }

    static VFXGraph CheckedSubgraph(VFXGraph g, string path, string subgraphPath)
    {
        var subGraph = VfxGraphTool.LoadGraph(subgraphPath);
        if (subGraph == g || string.Equals(path, subgraphPath, StringComparison.OrdinalIgnoreCase))
            throw new Exception($"subgraph '{subgraphPath}' cannot reference itself");
        subGraph.BuildSubgraphDependencies();
        if (subGraph.subgraphDependencies.Any(d => d != null && string.Equals(AssetDatabase.GetAssetPath(d), path, StringComparison.OrdinalIgnoreCase)))
            throw new Exception($"subgraph reference '{subgraphPath}' would create a cyclic reference in '{path}'");
        return subGraph;
    }

    // ---- endpoints for link2 / setin ----

    static VFXSlot FindSlot(IEnumerable<VFXSlot> slots, string path)
    {
        var parts = path.Split('.');
        var slot = slots.FirstOrDefault(x => x.property.name == parts[0])
            ?? throw new Exception($"no slot '{parts[0]}'; slots: [{string.Join(", ", slots.Select(x => x.property.name))}]");
        for (int i = 1; i < parts.Length; i++)
        {
            var parent = slot;
            slot = parent.children.FirstOrDefault(x => x.property.name == parts[i])
                ?? throw new Exception($"no sub-slot '{parts[i]}' in '{parent.property.name}'; sub-slots: [{string.Join(", ", parent.children.Select(x => x.property.name))}]");
        }
        return slot;
    }

    static IVFXSlotContainer OpById(Dictionary<string, IVFXSlotContainer> ops, string id) =>
        ops.TryGetValue(id, out var o) ? o : throw new Exception($"no operator id '{id}' (made by addop/addsubgraph op in this script); ids: [{string.Join(", ", ops.Keys)}]");

    static VFXSlot OutSlot(VFXGraph g, Dictionary<string, IVFXSlotContainer> ops, string spec)
    {
        if (spec.StartsWith("P:")) return Param(g, spec.Substring(2)).outputSlots[0];
        if (spec.StartsWith("O:"))
        {
            var r = spec.Substring(2); int dot = r.IndexOf('.');
            var op = OpById(ops, dot < 0 ? r : r.Substring(0, dot));
            return dot < 0 ? op.outputSlots[0] : FindSlot(op.outputSlots, r.Substring(dot + 1));
        }
        throw new Exception("source must be P:<param> or O:<id>[.slot]: " + spec);
    }

    static VFXSlot InSlot(VFXGraph g, Dictionary<string, IVFXSlotContainer> ops, string spec)
    {
        if (spec.StartsWith("P:"))   // output interface parameter of a subgraph: an internal producer links into its input slot
        {
            var p = Param(g, spec.Substring(2));
            if (!p.isOutput) throw new Exception($"parameter '{p.exposedName}' is not an output (subparam out), so it has no input slot to target");
            return p.inputSlots[0];
        }
        if (spec.StartsWith("Op:"))
        {
            var r = spec.Substring(3); int dot = r.IndexOf('.');
            if (dot < 0) throw new Exception("operator input needs a slot: Op:<id>.<slot>");
            return FindSlot(OpById(ops, r.Substring(0, dot)).inputSlots, r.Substring(dot + 1));
        }
        if (spec.StartsWith("B:"))
        {
            var p = spec.Substring(2).Split('/');
            if (p.Length != 3) throw new Exception("block input is B:<ctx>/<block>/<slot>: " + spec);
            return VfxGraphTool.Input(Block(g, p[0], p[1]), p[2]);
        }
        if (spec.StartsWith("C:"))
        {
            var p = spec.Substring(2).Split('/');
            if (p.Length != 2) throw new Exception("context input is C:<ctx>/<slot>: " + spec);
            return VfxGraphTool.Input(Ctx(g, p[0]), p[1]);
        }
        throw new Exception("destination must be Op:<id>.<slot>, B:<ctx>/<block>/<slot>, C:<ctx>/<slot> or P:<output param>: " + spec);
    }

    static string SlotNames(IEnumerable<VFXSlot> slots) =>
        string.Join(", ", slots.Select(s => string.IsNullOrEmpty(s.property.name) ? "(unnamed)" : s.property.name));

    // ---- selectors ----

    static readonly Dictionary<string, VFXContext> s_NewCtx = new Dictionary<string, VFXContext>();   // addctx/addsubgraph ctx/clippaste ids, cleared per Apply
    static VfxClipboard.Snapshot s_Clipboard;   // clipcopy/clippaste clipboard: static so it survives open and Apply calls

    static VFXContext Ctx(VFXGraph g, string sel)
    {
        if (sel.StartsWith("#")) return s_NewCtx.TryGetValue(sel.Substring(1), out var nc) ? nc : throw new Exception("no context id '" + sel + "' (addctx/addsubgraph ctx/clippaste in this script)");
        // "<Kind>@<name>": the context whose name or system title is <name> (e.g. Spawn@spawn Circle U, Init@Asteroid)
        string byName = null;
        int at = sel.IndexOf('@');
        if (at >= 0) { byName = sel.Substring(at + 1); sel = sel.Substring(0, at); }
        var parts = sel.Split(':');
        var type = parts[0] switch
        {
            "Spawn" => VFXContextType.Spawner, "Init" => VFXContextType.Init, "Update" => VFXContextType.Update,
            "Output" => VFXContextType.Output, _ => throw new Exception("context must be Spawn/Init/Update/Output[:Type]")
        };
        var list = g.children.OfType<VFXContext>().Where(c => c.contextType == type);
        if (parts.Length > 1) list = list.Where(c => c.GetType().Name == parts[1]);
        if (byName != null)
        {
            var named = list.Where(c => c.label == byName || (c.GetData() is VFXDataParticle dp && dp.title == byName)).ToList();
            if (named.Count == 0) throw new Exception($"no {parts[0]} context named '{byName}'; names: [{string.Join(" | ", list.Select(c => c.label))}]");
            list = named;
        }
        // prefer contexts that are part of a connected system (have flow in)
        return list.OrderByDescending(c => c.inputFlowSlot.Any(s => s.link.Count > 0)).FirstOrDefault()
               ?? throw new Exception("no context " + sel);
    }

    // block by exact name, else by substring; "name#n" = the n-th match (0-based) when several blocks share a name
    static VFXBlock Block(VFXGraph g, string ctx, string name)
    {
        var c = Ctx(g, ctx);
        int n = 0, hash = name.LastIndexOf('#');
        if (hash > 0 && int.TryParse(name.Substring(hash + 1), out var k)) { n = k; name = name.Substring(0, hash); }
        var matches = c.children.Where(b => b.name == name).ToList();
        if (matches.Count == 0) matches = c.children.Where(b => b.name.Contains(name)).ToList();
        if (matches.Count <= n) throw new Exception($"no block '{name}'#{n} in {ctx}; blocks: [{string.Join(" | ", c.children.Select(b => b.name))}]");
        return matches[n];
    }

    static IVFXSlotContainer Op(VFXGraph g, string type)
    {
        var parts = type.Split('#');
        int n = parts.Length > 1 ? int.Parse(parts[1]) : 0;
        return g.children.OfType<IVFXSlotContainer>().Where(o => o.GetType().Name == parts[0]).Skip(n).FirstOrDefault()
            ?? throw new Exception("no operator " + type);
    }

    static VFXParameter Param(VFXGraph g, string name) =>
        g.children.OfType<VFXParameter>().FirstOrDefault(p => p.exposedName == name)
        ?? throw new Exception($"no parameter '{name}'; parameters: [{string.Join(", ", g.children.OfType<VFXParameter>().Select(p => p.exposedName))}]");

    // clipcopy item: P:<param>, C:<ctx>, B:<ctx>/<block>, or O:/Op: followed by an
    // addop-style script id or an Op() selector (OperatorType[#n]). B: must be used alone.
    static VFXModel ClipItem(VFXGraph g, Dictionary<string, IVFXSlotContainer> ops, string spec)
    {
        if (spec.StartsWith("P:")) return Param(g, spec.Substring(2));
        if (spec.StartsWith("C:")) return Ctx(g, spec.Substring(2));
        if (spec.StartsWith("B:"))
        {
            var parts = spec.Substring(2).Split('/');
            if (parts.Length != 2) throw new Exception("clipcopy block item is B:<ctx>/<block>: " + spec);
            return Block(g, parts[0], parts[1]);
        }
        var id = spec;
        if (id.StartsWith("Op:")) id = id.Substring(3);
        else if (id.StartsWith("O:")) id = id.Substring(2);
        else throw new Exception("clipcopy item must be O:<id|Type[#n]>, P:<param>, C:<ctx> or B:<ctx>/<block>: " + spec);
        if (id.StartsWith("Op:")) id = id.Substring(3);   // tolerate O:Op:<Type>[#n]
        var m = (ops.TryGetValue(id, out var o) ? o : Op(g, id)) as VFXModel;
        return m ?? throw new Exception("clipcopy item is not an operator: " + spec);
    }

    // ---- tosubgraph helpers ----

    // Same slot-path layout as VfxClipboard.SlotPath/FetchSlot, resolved on the pasted clone.
    static VFXSlot CloneSlot(Dictionary<VFXModel, VFXModel> clone, VFXSlot slot, bool input)
    {
        if (!(slot.owner is VFXModel owner) || !clone.TryGetValue(owner, out var copy))
            throw new Exception("a tosubgraph boundary slot has no copied owner");
        var source = (IVFXSlotContainer)owner;
        var path = new List<int>();
        var current = slot;
        while (current.GetParent() != null) { path.Add(current.GetParent().GetIndex(current)); current = current.GetParent(); }
        int index = ReferenceEquals(current, source.activationSlot) ? -2 : (input ? source.inputSlots : source.outputSlots).IndexOf(current);
        path.Add(index);
        var target = (IVFXSlotContainer)copy;
        var result = index == -2 ? target.activationSlot : input ? target.GetInputSlot(index) : target.GetOutputSlot(index);
        for (int i = path.Count - 2; result != null && i >= 0; --i)
            result = result.GetNbChildren() > path[i] ? result[path[i]] : null;
        return result ?? throw new Exception($"the copied node has no slot matching '{slot.property.name}'");
    }

    static string InterfaceName(VFXSlot slot)
    {
        var name = slot.property.name;
        if (string.IsNullOrEmpty(name)) name = (slot.owner as VFXModel)?.name;
        return string.IsNullOrEmpty(name) ? "Interface" : name;
    }

    // Interface slot names must be valid expressions: no parentheses or spaces.
    static string SanitizeInterfaceName(string name) => name.Replace('(', '_').Replace(')', '_').Replace(' ', '_');

    static VFXParameter NewInterfaceParam(VFXGraph graph, Type type, VFXSlot like, string wanted, bool output)
    {
        var names = new HashSet<string>(graph.children.OfType<VFXParameter>().Select(x => x.exposedName), StringComparer.OrdinalIgnoreCase);
        var baseName = SanitizeInterfaceName(string.IsNullOrEmpty(wanted) ? "Interface" : wanted);
        var name = baseName; int k = 1;
        while (!names.Add(name)) name = baseName + (++k);
        var p = ScriptableObject.CreateInstance<VFXParameter>();
        p.Init(type); graph.AddChild(p);
        if (output) p.isOutput = true;
        p.SetSettingValue("m_ExposedName", name);
        if (!output) p.SetSettingValue("m_Exposed", true);
        var slot = output ? p.inputSlots[0] : p.outputSlots[0];
        if (slot.spaceable && like.spaceable) slot.space = like.space;
        return p;
    }

    static VFXSlot InterfaceSlot(IEnumerable<VFXSlot> slots, string name) =>
        slots.FirstOrDefault(x => x.property.name == name)
        ?? throw new Exception($"the new subgraph node has no interface slot '{name}'; slots: [{string.Join(", ", slots.Select(x => x.property.name))}]");

    // ---- values ----

    static void SetSlot(IVFXSlotContainer m, string kv)
    {
        int eq = kv.IndexOf('=');
        var slot = VfxGraphTool.Input(m, kv.Substring(0, eq));
        slot.value = ParseValue(kv.Substring(eq + 1), slot.property.type);
    }

    static Type ParamType(string s) => s switch
    {
        "float" => typeof(float), "int" => typeof(int), "bool" => typeof(bool), "vector3" => typeof(Vector3),
        "mesh" => typeof(Mesh), "texture2d" => typeof(Texture2D), "gradient" => typeof(Gradient),
        "curve" => typeof(AnimationCurve), "skinnedmesh" => typeof(SkinnedMeshRenderer), "transform" => typeof(UnityEditor.VFX.Transform),
        _ => throw new Exception("param type must be float|int|bool|vector3|mesh|texture2d|gradient|curve|skinnedmesh|transform")
    };

    // custom-attribute type words -> the real VFXValueType enum members (UnityEngine.VFX namespace)
    static UnityEngine.VFX.VFXValueType AttrValueType(string s) => s switch
    {
        "float" => UnityEngine.VFX.VFXValueType.Float,
        "int" => UnityEngine.VFX.VFXValueType.Int32,
        "uint" => UnityEngine.VFX.VFXValueType.Uint32,
        "bool" => UnityEngine.VFX.VFXValueType.Boolean,
        "vector2" => UnityEngine.VFX.VFXValueType.Float2,
        "vector3" => UnityEngine.VFX.VFXValueType.Float3,
        "vector4" => UnityEngine.VFX.VFXValueType.Float4,
        _ => throw new Exception("attr type must be float|int|uint|bool|vector2|vector3|vector4")
    };

    // strip the surrounding double quotes a key=value value may carry after tokenization (e.g. tooltip="text")
    static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;

    // name=value on a model's settings (not slots); falls back to the context's data (e.g. capacity).
    static void ApplySetting(VFXModel m, string kv)
    {
        int eq = kv.IndexOf('='); string n = kv.Substring(0, eq), v = kv.Substring(eq + 1);
        FieldInfo f = null;
        for (var ty = m.GetType(); f == null && ty != null; ty = ty.BaseType) f = ty.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        VFXModel target = m;
        if (f == null && m is VFXContext cx && cx.GetData() != null)
        {
            target = cx.GetData();
            for (var ty = target.GetType(); f == null && ty != null; ty = ty.BaseType) f = ty.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        if (f == null) throw new Exception("no setting '" + n + "' on " + m.GetType().Name);
        object val;
        if (v.StartsWith("asset:"))
        {
            var ap = v.Substring(6);
            val = AssetDatabase.LoadAllAssetsAtPath(ap).FirstOrDefault(o => f.FieldType.IsInstanceOfType(o)) ?? throw new Exception($"no {f.FieldType.Name} in {ap}");
        }
        else if (f.FieldType.IsEnum) val = Enum.Parse(f.FieldType, v);
        else if (f.FieldType == typeof(Vector2)) { var xy = v.Trim('(', ')').Split(','); val = new Vector2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture)); }
        else val = Convert.ChangeType(v, f.FieldType, CultureInfo.InvariantCulture);
        target.SetSettingValue(n, val);
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static object ParseValue(string v, Type type)
    {
        if (v.StartsWith("asset:"))
        {
            var p = v.Substring(6);
            var objs = AssetDatabase.LoadAllAssetsAtPath(p);
            return objs.FirstOrDefault(o => type.IsInstanceOfType(o)) ?? throw new Exception($"no {type.Name} in {p}");
        }
        if (type != null && type.Name == "FlipBook")   // (columns,rows)
        {
            var xy = v.Trim('(', ')').Split(','); object fb = Activator.CreateInstance(type);
            foreach (var (n, i) in new[] { ("x", 0), ("y", 1) })
            {
                var fi = type.GetField(n);
                fi.SetValue(fb, Convert.ChangeType(float.Parse(xy[i], Inv), fi.FieldType, CultureInfo.InvariantCulture));
            }
            return fb;
        }
        if (v.StartsWith("aabox("))
        {
            var parts = v.Substring(6).TrimEnd(')').Split(';');
            return new AABox { center = Vec(parts[0]), size = Vec(parts[1]) };
        }
        if (v == "gradient:white-fade")
        {
            var gr = new Gradient();
            gr.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                       new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
            return gr;
        }
        if (v.StartsWith("gradient:"))   // gradient:time=r,g,b,a;time=r,g,b,a  (HDR colours > 1 allowed; alpha keyed at the same times)
        {
            var keys = v.Substring(9).Split(';').Select(k => k.Split('=')).Select(k => (t: float.Parse(k[0], Inv), c: k[1].Split(',').Select(x => float.Parse(x, Inv)).ToArray())).ToArray();
            var gr = new Gradient { mode = GradientMode.Blend };
            gr.SetKeys(keys.Select(k => new GradientColorKey(new Color(k.c[0], k.c[1], k.c[2]), k.t)).ToArray(),
                       keys.Select(k => new GradientAlphaKey(k.c.Length > 3 ? k.c[3] : 1f, k.t)).ToArray());
            return gr;
        }
        if (v.StartsWith("curve:"))   // curve:time=value;time=value  (smooth keys)
        {
            var keys = v.Substring(6).Split(';').Select(k => k.Split('='))
                        .Select(k => new Keyframe(float.Parse(k[0], Inv), float.Parse(k[1], Inv))).ToArray();
            var c = new AnimationCurve(keys);
            for (int i = 0; i < c.length; i++) c.SmoothTangents(i, 0f);
            return c;
        }
        if (type == typeof(float)) return float.Parse(v, Inv);
        if (type == typeof(int)) return int.Parse(v, Inv);
        if (type == typeof(uint)) return uint.Parse(v, Inv);
        if (type == typeof(bool)) return bool.Parse(v);
        if (type == typeof(Vector3) || type.Name == "Position" || type.Name == "Vector" || type.Name == "DirectionType") return Vec(v.Trim('(', ')'));
        if (type == typeof(Vector2)) { var f = v.Trim('(', ')').Split(','); return new Vector2(float.Parse(f[0], Inv), float.Parse(f[1], Inv)); }
        throw new Exception($"don't know how to parse '{v}' as {type.Name}");
    }

    static Vector3 Vec(string s)
    {
        var f = s.Trim('(', ')').Split(',');
        return new Vector3(float.Parse(f[0], Inv), float.Parse(f[1], Inv), float.Parse(f[2], Inv));
    }

    static List<string> Tokenize(string line) =>
        // "quoted name" | key="quoted value" (kept as key=value) | bare token
        Regex.Matches(line, "\"([^\"]*)\"|([^\\s=\"]+=)\"([^\"]*)\"|(\\S+)").Cast<Match>().Select(m =>
            m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value + m.Groups[3].Value : m.Groups[4].Value).ToList();

    // ---- custom HLSL ----

    static void Hlsl(VFXGraph g, List<string> t, StringBuilder log, int lineNo)
    {
        var c = Ctx(g, t[1]);
        string file = t.FirstOrDefault(x => x.StartsWith("file="))?.Substring(5) ?? throw new Exception("hlsl needs file=");
        string fn = t.FirstOrDefault(x => x.StartsWith("function="))?.Substring(9) ?? throw new Exception("hlsl needs function=");
        var include = AssetDatabase.LoadAssetAtPath<ShaderInclude>(file) ?? throw new Exception("no ShaderInclude at " + file);
        var block = c.children.OfType<UnityEditor.VFX.Block.CustomHLSL>().FirstOrDefault(b => b.name == t[2]);
        // relink existing inputs by name after the swap
        var links = block == null ? new List<(string, VFXSlot)>() :
            block.inputSlots.SelectMany(s => s.LinkedSlots.Select(l => (s.property.name, l))).ToList();
        if (block == null)
        {
            block = ScriptableObject.CreateInstance<UnityEditor.VFX.Block.CustomHLSL>();
            block.SetSettingValue("m_BlockName", t[2]);
            c.AddChild(block);
        }
        block.SetSettingValue("m_ShaderFile", include);
        VfxGraphTool.SelectHlslFunction(block, fn);
        foreach (var (name, other) in links)
        {
            var s = block.inputSlots.FirstOrDefault(x => x.property.name == name);
            if (s != null && !s.HasLink()) s.Link(other);
        }
        log.AppendLine($"{lineNo}: hlsl '{t[2]}' in {t[1]} <- {file}:{fn}; inputs: {string.Join(", ", block.inputSlots.Select(s => s.property.name + (s.HasLink() ? "(linked)" : "")))}");
    }

    static string CompileReport(VFXGraph g)
    {
        var sb = new StringBuilder();
        var getMode = typeof(VFXGraph).GetMethod("GetCompilationMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var setMode = typeof(VFXGraph).GetMethod("SetCompilationMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object old = getMode.Invoke(g, null);
        try
        {
            setMode.Invoke(g, new[] { Enum.Parse(old.GetType(), "Runtime") });
            g.PrepareGraph();
            g.errorManager.RefreshCompilationReport();
            var output = g.Compile();
            object desc = output.assetDesc;
            var systems = desc.GetType().GetField("systemDesc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(desc) as Array;
            var shaders = desc.GetType().GetField("shaderSourceDesc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(desc) as Array;
            sb.Append($"compile: success={output.success} systems={systems?.Length} shaders={shaders?.Length}");
            var rep = g.errorManager.compileReporter;
            foreach (var m in new VFXModel[] { g }.Concat(g.children).Concat(g.children.OfType<VFXContext>().SelectMany(c => c.children)))
                foreach (var e in rep.GetDirtyModelErrors(m)) sb.Append($"\n  ERR [{m.name}] {e.type}: {e.error} {e.description}");
        }
        finally { setMode.Invoke(g, new[] { old }); }
        return sb.ToString();
    }
}
