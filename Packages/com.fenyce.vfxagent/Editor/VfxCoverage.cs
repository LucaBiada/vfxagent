// Library coverage counter for VFX Graph: creates every context, operator and block from VFXLibrary
// into a fresh in-memory graph and reports which ones fail. Never saves anything; the temp graph and
// its children are destroyed at the end.
//
// It also reports two ways a descriptor can be created from outside (each listed separately):
//   "by type name": the addctx/addop/addblock ops create a node from a type NAME only (FindType: first
//     non-abstract type with Type.Name == name assignable to the base class, then ScriptableObject.CreateInstance).
//     A descriptor is NOT reachable that way when
//       (1) its variant carries preset settings (plain CreateInstance gives a different node than the descriptor), or
//       (2) two descriptors of the same kind share the same modelType (the name alone is ambiguous), or
//       (3) FindType by modelType.Name would return a different type.
//   "by addvariant": the addvariant op creates a node from the descriptor's VFXLibrary name (d.name); when several
//     descriptors of a kind share a name, the op's trailing 'name#k' (k = 1-based position among same-name
//     descriptors of the kind) picks the k-th one.
//     A descriptor is reachable via addvariant when CreateInstance() of THAT descriptor (reached by its name, or
//     name#k when the name is shared) returns a model whose GetType() equals the descriptor's modelType.
//     Checked by reimplementing that lookup privately below, with a try/catch per descriptor; the report states
//     the name#k form used for each duplicate name.
//
// Call from `unity command eval`:  return VfxCoverage.Run(@"C:/.../coverage.md");
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.VFX;
using UnityEngine;

public static class VfxCoverage
{
    class ReachResult
    {
        public string kind;
        public string name;
        public Type modelType;
        public List<int> reasons = new List<int>();
        public bool Reachable => reasons.Count == 0;
    }

    class VariantResult
    {
        public string kind;
        public string name;
        public int k;          // 1-based position among same-name descriptors of the kind (1 when the name is unique)
        public string form;    // the addvariant form that reaches this descriptor: name, or name#k when the name is shared
        public Type modelType;
        public List<string> reasons = new List<string>();
        public bool Reachable => reasons.Count == 0;
    }

    // A successfully created operator, kept so the wiring check can inspect its live slots.
    class OpCreated
    {
        public VFXModelDescriptor<VFXOperator> desc;
        public VFXOperator op;
    }

    // One candidate link target for the wiring check: an input slot (top-level, or a child slot in the
    // fallback tier) together with the model that owns it. `owner` is compared by instance identity to
    // skip self-wiring (the task asks for "any OTHER operator"); `type` is the slot's property.type, the
    // only value CanConvertFrom depends on (never instance state), used to try at most one candidate per
    // distinct type per source.
    class WireCandidate
    {
        public VFXSlot slot;
        public VFXModel owner;
        public Type type;
    }

    public static string Run(string reportPath)
    {
        var failures = new List<string>();
        var graph = ScriptableObject.CreateInstance<VFXGraph>();
        int ctxOk = 0, ctxFail = 0, opOk = 0, opFail = 0, blkOk = 0, blkFail = 0, blkNoCtx = 0;
        List<ReachResult> ctxReach = null, opReach = null, blkReach = null;
        List<VariantResult> ctxVar = null, opVar = null, blkVar = null;
        // wiring (see the wiring section below)
        int wireOk = 0, wireNoOutput = 0, wireNoProbe = 0, wireExc = 0;
        var wireNoProbeList = new List<string>();
        var wireExcList = new List<string>();
        var createdOps = new List<OpCreated>();
        var blockInstances = new List<VFXModel>(); // fresh block candidates, destroyed in finally
        try
        {
            VFXLibrary.Load();

            var ctxDescs = VFXLibrary.GetContexts().ToList();
            var opDescs = VFXLibrary.GetOperators().ToList();
            var blkDescs = VFXLibrary.GetBlocks().ToList();

            // reachability analysis, independent of the creation below
            ctxReach = Analyze<VFXContext>("context", ctxDescs, typeof(VFXContext));
            opReach = Analyze<VFXOperator>("operator", opDescs, typeof(VFXOperator));
            blkReach = Analyze<VFXBlock>("block", blkDescs, typeof(VFXBlock));
            ctxVar = AnalyzeVariant<VFXContext>("context", ctxDescs);
            opVar = AnalyzeVariant<VFXOperator>("operator", opDescs);
            blkVar = AnalyzeVariant<VFXBlock>("block", blkDescs);

            // contexts: create and attach to the graph root
            foreach (var d in ctxDescs)
            {
                try { var c = d.CreateInstance(); graph.AddChild(c); ctxOk++; }
                catch (Exception e) { ctxFail++; failures.Add($"context '{d.name}' ({d.modelType.Name}): {Short(e)}"); }
            }

            // operators: create, attach, then resync so numeric operators build their output slots
            foreach (var d in opDescs)
            {
                try
                {
                    var op = d.CreateInstance(); graph.AddChild(op); op.ResyncSlots(true); opOk++;
                    createdOps.Add(new OpCreated { desc = d, op = op });
                }
                catch (Exception e) { opFail++; failures.Add($"operator '{d.name}' ({d.modelType.Name}): {Short(e)}"); }
            }

            // blocks: create and attach to the first context that accepts them (compatibleContexts / compatibleData)
            var contexts = graph.children.OfType<VFXContext>().ToList();
            foreach (var d in blkDescs)
            {
                try
                {
                    var b = d.CreateInstance();
                    var host = contexts.FirstOrDefault(c => c.AcceptChild(b));
                    if (host == null) { blkNoCtx++; continue; }
                    host.AddChild(b); blkOk++;
                }
                catch (Exception e) { blkFail++; failures.Add($"block '{d.name}' ({d.modelType.Name}): {Short(e)}"); }
            }

            // wiring: can each created operator's FIRST OUTPUT slot actually be linked into SOME input slot
            // of some other model? One comprehensive candidate list (no 40-operator limit):
            //   (a) every created operator (reusing the live instances in createdOps, already created and
            //       ResyncSlots'd above) - ALL of its top-level input slots, plus their child slots as a
            //       fallback tier tried only when no top-level slot links;
            //   (b) every block descriptor - one fresh CreateInstance() each, its input slots (and their
            //       children as fallback).
            // Speed guard: for an output->input pair CanLink/Link reduces to input.CanConvertFrom(outType)
            // and every CanConvertFrom depends only on the slot's property.type (never instance state), so
            // at most one candidate per distinct property.type is tried per source. Candidates of (a) are
            // graph children (destroyed by the finally below); the fresh block instances of (b) are in
            // blockInstances and destroyed there too. Nothing is saved.
            var candTop = new List<WireCandidate>();      // top-level input slots, tried first
            var candFallback = new List<WireCandidate>(); // child slots, only if no top-level slot links
            AddInputCandidates(candTop, candFallback, createdOps.Select(c => c.op).ToList());
            foreach (var d in blkDescs)
            {
                try
                {
                    var b = d.CreateInstance();
                    blockInstances.Add(b);
                    b.ResyncSlots(true); // ensure inputSlots are populated even if OnEnable didn't sync them
                    AddInputCandidates(candTop, candFallback, new VFXModel[] { b });
                }
                catch { } // block unusable; skip it
            }
            foreach (var c in createdOps)
            {
                string outTypeName = null;
                try
                {
                    var outSlots = c.op.outputSlots;
                    if (outSlots == null || outSlots.Count == 0) { wireNoOutput++; continue; } // no output slot: not a failure
                    var outSlot = outSlots[0];
                    outTypeName = TypeName(outSlot.property.type);
                    bool linked = TryWire(outSlot, candTop, c.op) || TryWire(outSlot, candFallback, c.op);
                    if (linked) wireOk++;
                    else { wireNoProbe++; wireNoProbeList.Add($"'{c.desc.name}' ({c.desc.modelType?.Name}, out {outTypeName})"); }
                }
                catch (Exception e)
                {
                    wireExc++;
                    wireExcList.Add($"'{c.desc.name}' ({c.desc.modelType?.Name}{(outTypeName != null ? ", out " + outTypeName : "")}): {Short(e)}");
                }
            }
        }
        finally
        {
            foreach (var b in blockInstances) UnityEngine.Object.DestroyImmediate(b);
            foreach (var c in graph.children.OfType<VFXContext>().ToList())
                foreach (var b in c.children.ToArray()) UnityEngine.Object.DestroyImmediate(b);
            foreach (var m in graph.children.ToArray()) UnityEngine.Object.DestroyImmediate(m);
            UnityEngine.Object.DestroyImmediate(graph);
        }

        int ctxYes = CountReachable(ctxReach), opYes = CountReachable(opReach), blkYes = CountReachable(blkReach);
        int ctxNo = Count(ctxReach) - ctxYes, opNo = Count(opReach) - opYes, blkNo = Count(blkReach) - blkYes;
        int ctxVYes = CountReachable(ctxVar), opVYes = CountReachable(opVar), blkVYes = CountReachable(blkVar);
        int ctxVNo = Count(ctxVar) - ctxVYes, opVNo = Count(opVar) - opVYes, blkVNo = Count(blkVar) - blkVYes;
        var notByName = Collect(ctxReach, opReach, blkReach).Where(r => !r.Reachable).ToList();
        var notByVariant = CollectV(ctxVar, opVar, blkVar).Where(r => !r.Reachable).ToList();
        var byPosition = CollectV(ctxVar, opVar, blkVar).Where(r => r.k > 1).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# VFX library coverage");
        sb.AppendLine();
        sb.AppendLine("| kind | OK | failed | no compatible context |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| context | {ctxOk} | {ctxFail} | - |");
        sb.AppendLine($"| operator | {opOk} | {opFail} | - |");
        sb.AppendLine($"| block | {blkOk} | {blkFail} | {blkNoCtx} |");
        sb.AppendLine();
        sb.AppendLine("## Reachable");
        sb.AppendLine();
        sb.AppendLine("| kind | by type name (yes/no) | by addvariant (yes/no) |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| context | {ctxYes}/{ctxYes + ctxNo} | {ctxVYes}/{ctxVYes + ctxVNo} |");
        sb.AppendLine($"| operator | {opYes}/{opYes + opNo} | {opVYes}/{opVYes + opVNo} |");
        sb.AppendLine($"| block | {blkYes}/{blkYes + blkNo} | {blkVYes}/{blkVYes + blkVNo} |");
        sb.AppendLine();
        sb.AppendLine("`by type name`: the `addctx`/`addop`/`addblock` type-name path (FindType + CreateInstance) reproduces the descriptor: no preset settings (reason 1), no shared modelType (reason 2), and FindType resolves its own type (reason 3).");
        sb.AppendLine("`by addvariant`: the descriptor's VFXLibrary name reaches it - a unique name as-is, a shared name via the position form `name#k` (k = 1-based position among same-name descriptors of the kind) - and CreateInstance of that descriptor returns a model of the descriptor's modelType. Experimental entries are hidden from the lookup unless VFXViewPreference.displayExperimentalOperator is on (same filter as the list above).");
        sb.AppendLine();
        if (notByName.Count == 0) sb.AppendLine("All descriptors are reachable by type name.");
        else
        {
            sb.AppendLine("### Not reachable by type name");
            foreach (var r in notByName)
                sb.AppendLine($"- {r.kind} '{r.name}' ({r.modelType?.Name}): reason(s) {string.Join(", ", r.reasons)}");
        }
        sb.AppendLine();
        if (notByVariant.Count == 0) sb.AppendLine("All descriptors are reachable via addvariant.");
        else
        {
            sb.AppendLine("### Not reachable via addvariant");
            foreach (var r in notByVariant)
                sb.AppendLine($"- {r.kind} '{r.form}' ({r.modelType?.Name}): {string.Join("; ", r.reasons)}");
        }
        if (byPosition.Count > 0)
        {
            sb.AppendLine("### Duplicate names (position form used)");
            foreach (var r in byPosition)
                sb.AppendLine($"- {r.kind} '{r.name}' ({r.modelType?.Name}): use '{r.form}'");
        }
        sb.AppendLine();
        sb.AppendLine("## Wiring");
        sb.AppendLine();
        sb.AppendLine("Can each created operator's first OUTPUT slot actually be linked into some input slot of another model (`CanLink` + real `Link`, verified with `HasLink`)? Candidates: every top-level input slot of every created operator (live instances reused) plus every input slot of a fresh `CreateInstance()` of each block descriptor; child slots are a fallback tier tried only when no top-level slot links; at most one candidate per distinct input slot `property.type` is tried per source (`CanConvertFrom` depends only on it); self-wiring excluded by instance identity. No output slot is not a failure.");
        sb.AppendLine();
        sb.AppendLine("| result | count |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| linked OK | {wireOk} |");
        sb.AppendLine($"| no output | {wireNoOutput} |");
        sb.AppendLine($"| no compatible input slot | {wireNoProbe} |");
        sb.AppendLine($"| exception | {wireExc} |");
        sb.AppendLine($"| total operators checked | {createdOps.Count} |");
        if (wireNoProbeList.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### No compatible input slot (first output slot exists, but no candidate accepted it; `out` = the output slot's property type)");
            foreach (var n in wireNoProbeList) sb.AppendLine("- " + n);
        }
        if (wireExcList.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Wiring exceptions");
            foreach (var n in wireExcList) sb.AppendLine("- " + n);
        }
        sb.AppendLine();
        if (failures.Count == 0) sb.AppendLine("No creation failures.");
        else
        {
            sb.AppendLine("## Creation failures");
            foreach (var f in failures) sb.AppendLine("- " + f);
        }
        System.IO.File.WriteAllText(reportPath, sb.ToString());
        return "by type name: " + $"{ctxYes}/{ctxYes + ctxNo} contexts, {opYes}/{opYes + opNo} operators, {blkYes}/{blkYes + blkNo} blocks; " +
               $"by addvariant: {ctxVYes}/{ctxVYes + ctxVNo} contexts, {opVYes}/{opVYes + opVNo} operators, {blkVYes}/{blkVYes + blkVNo} blocks\n" +
               $"coverage: {ctxOk} contexts, {opOk} operators, {blkOk} blocks OK; {ctxFail + opFail + blkFail} failed, {blkNoCtx} blocks without context; " +
               $"wiring: {wireOk} linked OK, {wireNoOutput} no output, {wireNoProbe} no compatible input slot, {wireExc} exception (of {createdOps.Count} operators) -> {reportPath}";
    }

    // For every descriptor of one kind, reachable = none of the three FindType-ambiguity conditions holds.
    static List<ReachResult> Analyze<T>(string kind, IEnumerable<VFXModelDescriptor<T>> descs, Type baseType) where T : VFXModel
    {
        var list = descs?.ToList() ?? new List<VFXModelDescriptor<T>>();
        var byType = list.GroupBy(d => d.modelType).ToDictionary(g => g.Key, g => g.Count());
        var results = new List<ReachResult>();
        foreach (var d in list)
        {
            var r = new ReachResult { kind = kind, name = d.name, modelType = d.modelType };
            if (d.variant != null && d.variant.settings != null && d.variant.settings.Length > 0) r.reasons.Add(1);
            if (d.modelType != null && byType.TryGetValue(d.modelType, out var n) && n > 1) r.reasons.Add(2);
            if (d.modelType != null && FindType(d.modelType.Name, baseType) != d.modelType) r.reasons.Add(3);
            results.Add(r);
        }
        return results;
    }

    // For every descriptor of one kind, reachable via addvariant = CreateInstance() of that exact descriptor
    // (reached by its name, or name#k when the name is shared - k = 1-based position among same-name descriptors
    // of the kind, in the kind's list order) returns a model of its modelType.
    // Reimplemented from VfxGraphOps.PickVariant (private there): VFXLibrary.Load() already called by Run();
    // each descriptor's own CreateInstance() (applies the descriptor's variant settings) is checked in its own
    // try/catch (reason c), and the created models are destroyed afterwards.
    static List<VariantResult> AnalyzeVariant<T>(string kind, IEnumerable<VFXModelDescriptor<T>> descs) where T : VFXModel
    {
        var list = descs?.ToList() ?? new List<VFXModelDescriptor<T>>();
        var seen = new Dictionary<string, int>();
        var created = new List<VFXModel>();
        var results = new List<VariantResult>();
        try
        {
            foreach (var d in list)
            {
                int k = seen.TryGetValue(d.name, out var c) ? c + 1 : 1;
                seen[d.name] = k;
                var r = new VariantResult { kind = kind, name = d.name, k = k, form = k > 1 ? d.name + "#" + k : d.name, modelType = d.modelType };
                try
                {
                    var m = d.CreateInstance();
                    created.Add(m);
                    if (m.GetType() != d.modelType) r.reasons.Add($"b type mismatch (CreateInstance returned {m.GetType().Name})");
                }
                catch (Exception e) { r.reasons.Add("c exception: " + Short(e)); }
                results.Add(r);
            }
        }
        finally
        {
            foreach (var m in created) UnityEngine.Object.DestroyImmediate(m);
        }
        return results;
    }

    // Reimplemented from VfxGraphOps.FindType: first non-abstract type, across all loaded assemblies,
    // whose Type.Name equals name and is assignable to baseType.
    static Type FindType(string name, Type baseType)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var x in types) if (x.Name == name && baseType.IsAssignableFrom(x) && !x.IsAbstract) return x;
        }
        return null;
    }

    static int Count(List<ReachResult> list) => list == null ? 0 : list.Count;
    static int CountReachable(List<ReachResult> list) => list == null ? 0 : list.Count(r => r.Reachable);
    static IEnumerable<ReachResult> Collect(params List<ReachResult>[] lists) => lists.Where(l => l != null).SelectMany(l => l);
    static int Count(List<VariantResult> list) => list == null ? 0 : list.Count;
    static int CountReachable(List<VariantResult> list) => list == null ? 0 : list.Count(r => r.Reachable);
    static IEnumerable<VariantResult> CollectV(params List<VariantResult>[] lists) => lists.Where(l => l != null).SelectMany(l => l);

    // Collect every top-level input slot (and its child slots, for the fallback tier) of the given
    // slot-container models as wiring candidates.
    static void AddInputCandidates(List<WireCandidate> top, List<WireCandidate> fallback, IEnumerable<VFXModel> models)
    {
        foreach (var m in models)
        {
            try
            {
                var container = m as IVFXSlotContainer; // operators are VFXSlotContainerModel<VFXModel,VFXModel>, blocks <VFXContext,VFXModel> - the interface covers both
                if (container == null) continue;
                var ins = container.inputSlots;
                if (ins == null) continue;
                foreach (var s in ins)
                {
                    if (s == null) continue;
                    top.Add(new WireCandidate { slot = s, owner = m, type = s.property.type });
                    var kids = s.children;
                    if (kids != null)
                        foreach (var k in kids)
                            if (k != null) fallback.Add(new WireCandidate { slot = k, owner = m, type = k.property.type });
                }
            }
            catch { } // model unusable; skip it
        }
    }

    // Try to link outSlot into the first candidate that accepts it: skip the source's own instance,
    // try at most one candidate per distinct property.type (CanConvertFrom depends only on it), clean
    // the candidate's stale links, CanLink + real Link, verify with HasLink.
    static bool TryWire(VFXSlot outSlot, List<WireCandidate> cands, VFXModel self)
    {
        var triedTypes = new HashSet<Type>();
        foreach (var cand in cands)
        {
            if (cand.slot == null || cand.owner == self) continue; // "any OTHER operator" - skip self instance
            if (cand.type != null && !triedTypes.Add(cand.type)) continue; // already tried this input slot type
            try { cand.slot.UnlinkAll(false, false); } catch { } // clean state so HasLink() below is meaningful
            bool canLink; try { canLink = outSlot.CanLink(cand.slot); } catch { continue; }
            if (!canLink) continue;
            bool ok; try { ok = outSlot.Link(cand.slot); } catch { continue; }
            if (ok && cand.slot.HasLink()) return true;
        }
        return false;
    }

    static string TypeName(Type t) => t == null ? "null" : t.Name;

    static string Short(Exception e) =>
        (e is TargetInvocationException ti ? ti.InnerException : e).GetType().Name + ": " + (e.Message ?? "").Replace("\n", " ");
}
