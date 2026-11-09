// Editor-only VFX Graph authoring/diagnostics helper, meant to be driven from `unity command eval`.
// The assembly is named "Unity.Testing.VisualEffectGraph.Editor" on purpose: the VFX Graph package grants that name
// access to its internal editor API (InternalsVisibleTo in com.unity.visualeffectgraph/Editor/PackageInfo.cs), the same
// API the VFX Graph window uses. Nothing here ships in builds. Tied to VFX Graph internals: may need fixes on upgrades.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.VFX;
using UnityEngine;

public static class VfxGraphTool
{
    // ---------- access ----------

    /// The editable graph of a .vfx asset (the same instance the VFX Graph window edits).
    internal static VFXGraph LoadGraph(string assetPath)
    {
        // VisualEffectResource is engine-internal (UnityEditor.VFXModule) and not visible to this assembly: find it by name.
        var resType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEditor.VFX.VisualEffectResource")).FirstOrDefault(t => t != null);
        var getRes = resType.GetMethod("GetResourceAtPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        var resource = getRes.Invoke(null, new object[] { assetPath });
        if (resource == null) throw new ArgumentException("No VFX resource at " + assetPath);
        var ext = typeof(VFXGraph).Assembly.GetType("UnityEditor.VFX.VisualEffectResourceExtensions");
        var getGraph = ext.GetMethod("GetGraph", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var graph = getGraph.Invoke(null, new object[] { resource }) as VFXGraph;
        if (graph == null) throw new InvalidOperationException("Resource has no graph: " + assetPath);
        return graph;
    }

    // ---------- diagnostics ----------

    /// Human-readable dump of a graph: systems/contexts, flow links, blocks with settings and inputs, operators,
    /// exposed parameters, data links, and the errors the VFX Graph window would show (validation + compilation).
    public static string Describe(string assetPath, bool compile = true)
    {
        var graph = LoadGraph(assetPath);
        var sb = new StringBuilder();
        sb.AppendLine($"# {assetPath}");

        var ids = new Dictionary<VFXModel, string>();
        string Id(VFXModel m)
        {
            if (m == null) return "null";
            if (!ids.TryGetValue(m, out var s)) { s = (m is VFXContext ? "C" : m is VFXParameter ? "P" : "O") + ids.Count; ids[m] = s; }
            return s;
        }

        var contexts = graph.children.OfType<VFXContext>().ToList();
        var parameters = graph.children.OfType<VFXParameter>().ToList();
        var operators = graph.children.Where(c => !(c is VFXContext) && !(c is VFXParameter)).ToList();
        foreach (var c in contexts) Id(c);
        foreach (var p in parameters) Id(p);
        foreach (var o in operators) Id(o);

        // compile first so compile errors are available below
        var compileErrors = new Dictionary<VFXModel, List<string>>();
        bool? compiled = null;
        if (compile)
        {
            try
            {
                graph.errorManager.RefreshCompilationReport();
                var output = graph.Compile();
                compiled = output.success;
            }
            catch (Exception e) { sb.AppendLine("COMPILE EXCEPTION: " + e.GetType().Name + ": " + e.Message); }
        }

        sb.AppendLine($"compiled: {(compiled.HasValue ? (compiled.Value ? "OK" : "FAILED") : "skipped")}");
        sb.AppendLine();
        sb.AppendLine("## Contexts (flow)");
        foreach (var c in contexts)
        {
            var data = c.GetData();
            string dataDesc = data == null ? "-" : $"{data.GetType().Name}#{data.GetHashCode() % 10000}" + (data is VFXDataParticle dp ? $" title='{dp.title}'" : "");
            sb.AppendLine($"[{Id(c)}] {c.GetType().Name} \"{c.label}\" type={c.contextType} data={dataDesc}{SubgraphDescription(c)}");
            sb.AppendLine("    flow in : " + FlowList(c.inputFlowSlot, Id) + "   flow out: " + FlowList(c.outputFlowSlot, Id));
            AppendSettings(sb, c, "    ");
            AppendInputs(sb, c, Id, "    ");
            AppendErrors(sb, c, "    ");
            foreach (var b in c.children)
            {
                sb.AppendLine($"    - block {b.GetType().Name} \"{b.name}\"{(b.enabled ? "" : " (DISABLED)")}{SubgraphDescription(b)}");
                AppendSettings(sb, b, "        ");
                AppendInputs(sb, b, Id, "        ");
                AppendErrors(sb, b, "        ");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Exposed / graph parameters");
        foreach (var p in parameters)
        {
            string kind = p.isOutput ? "output" : p.exposed ? "EXPOSED" : "";
            sb.AppendLine($"[{Id(p)}] {(kind.Length > 0 ? kind + " " : "")}'{p.exposedName}' : {p.type?.Name} = {Short(p.value)}"
                          + (p.isOutput ? InputLink(p.inputSlots.FirstOrDefault(), Id) : ""));
            var meta = new List<string>
            {
                $"exposed={p.exposed}",
                $"category='{p.category ?? ""}'",
                $"order={p.order}"
            };
            if (!string.IsNullOrEmpty(p.tooltip)) meta.Add($"tooltip='{p.tooltip}'");
            meta.Add($"filter={p.valueFilter}");
            if (p.valueFilter == VFXValueFilter.Range) meta.Add($"min={Short(p.min)} max={Short(p.max)}");
            else if (p.valueFilter == VFXValueFilter.Enum) meta.Add($"enum=[{string.Join(", ", p.enumValues ?? new List<string>())}]");
            sb.AppendLine("    " + string.Join("  ", meta));
            foreach (var o in p.outputSlots) AppendLinks(sb, o, Id, "    out");
        }

        sb.AppendLine();
        sb.AppendLine("## Custom attributes");
        var attrs = graph.customAttributes.ToList();
        if (attrs.Count == 0) sb.AppendLine("  (none)");
        foreach (var a in attrs)
            sb.AppendLine($"  '{a.attributeName}' : {a.type}{(string.IsNullOrEmpty(a.description) ? "" : " '" + a.description + "'")}");

        sb.AppendLine();
        sb.AppendLine("## Operators");
        foreach (var o in operators)
        {
            sb.AppendLine($"[{Id(o)}] {o.GetType().Name} \"{o.name}\"{SubgraphDescription(o)}");
            AppendSettings(sb, o, "    ");
            if (o is IVFXSlotContainer sc) AppendInputs(sb, sc, Id, "    ");
            AppendErrors(sb, o, "    ");
        }

        // compile errors per model
        var reporter = graph.errorManager.compileReporter;
        if (reporter != null)
        {
            var lines = new List<string>();
            foreach (var m in new VFXModel[] { graph }.Concat(contexts).Concat(contexts.SelectMany(c => c.children)).Concat(operators).Concat(parameters))
                foreach (var e in reporter.GetDirtyModelErrors(m))
                    lines.Add($"  [{(ids.ContainsKey(m) ? ids[m] : m.GetType().Name)}] {e.type}: {e.error} {e.description}");
            sb.AppendLine();
            sb.AppendLine("## Compilation errors");
            sb.AppendLine(lines.Count == 0 ? "  (none)" : string.Join("\n", lines));
        }
        return sb.ToString();
    }

    /// Same as Describe(compile: true), but runs on the next editor tick and writes the report to a file, so a slow
    /// compile doesn't hit the CLI's 5 s main-thread limit. Poll for the file.
    public static string DescribeToFile(string assetPath, string outFile)
    {
        if (System.IO.File.Exists(outFile)) System.IO.File.Delete(outFile);
        EditorApplication.delayCall += () =>
        {
            string text;
            try { text = Describe(assetPath, true); }
            catch (Exception e) { text = "FAILED: " + e; }
            System.IO.File.WriteAllText(outFile, text);
        };
        return "scheduled -> " + outFile;
    }

    /// Forces a real import compile: drops the VFX window's cached "authoring" compile result for the asset (the importer
    /// reuses it instead of compiling, so a stale/empty cache keeps producing an empty asset), then reimports.
    /// Runs on the next editor tick and writes a report (incl. compiled system counts on scene VisualEffects) to outFile.
    public static string ForceReimportToFile(string assetPath, string outFile)
    {
        if (System.IO.File.Exists(outFile)) System.IO.File.Delete(outFile);
        EditorApplication.delayCall += () =>
        {
            var sb = new StringBuilder();
            var logs = new List<string>();
            Application.LogCallback cb = (m, st, t) => { if (t != LogType.Log) logs.Add(t + ": " + m); };
            Application.logMessageReceived += cb;
            try
            {
                VFXGraph.UnregisterAuthoringCompileData(AssetDatabase.GUIDFromAssetPath(assetPath));
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                sb.AppendLine("reimported " + assetPath);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.VFX.VisualEffectAsset>(assetPath);
                foreach (var vfx in UnityEngine.Object.FindObjectsByType<UnityEngine.VFX.VisualEffect>())
                {
                    if (vfx.visualEffectAsset != asset) continue;
                    vfx.Reinit();
                    var p = new List<string>(); vfx.GetParticleSystemNames(p);
                    var s = new List<string>(); vfx.GetSpawnSystemNames(s);
                    sb.AppendLine($"  {vfx.name}: particleSystems={p.Count} spawners={s.Count}");
                }
            }
            catch (Exception e) { sb.AppendLine("FAILED: " + e); }
            finally { Application.logMessageReceived -= cb; }
            sb.AppendLine("warnings/errors during import:" + (logs.Count == 0 ? " none" : "\n  " + string.Join("\n  ", logs)));
            System.IO.File.WriteAllText(outFile, sb.ToString());
        };
        return "scheduled -> " + outFile;
    }

    /// Compiles the graph the way the importer does (Runtime mode, PrepareGraph) and reports what came out:
    /// success flag, number of compiled systems (and their names/types), plus compile errors. Writes to outFile.
    public static string CompileReportToFile(string assetPath, string outFile)
    {
        if (System.IO.File.Exists(outFile)) System.IO.File.Delete(outFile);
        EditorApplication.delayCall += () =>
        {
            var sb = new StringBuilder();
            var logs = new List<string>();
            Application.LogCallback cb = (m, st, t) => { if (t != LogType.Log) logs.Add(t + ": " + m + (t == LogType.Exception ? "\n" + st : "")); };
            Application.logMessageReceived += cb;
            var graph = LoadGraph(assetPath);
            // VFXCompilationMode is engine-internal (not visible here): get/set it through reflection.
            var getMode = typeof(VFXGraph).GetMethod("GetCompilationMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setMode = typeof(VFXGraph).GetMethod("SetCompilationMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object oldMode = getMode.Invoke(graph, null);
            try
            {
                setMode.Invoke(graph, new[] { Enum.Parse(oldMode.GetType(), "Runtime") });
                sb.AppendLine($"compilation mode was {oldMode}, compiling as Runtime");
                graph.PrepareGraph();
                graph.errorManager.RefreshCompilationReport();
                var output = graph.Compile();
                sb.AppendLine($"runtime compile success={output.success}");
                object desc = output.assetDesc;
                foreach (var f in desc.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var v = f.GetValue(desc);
                    if (v is Array arr)
                    {
                        sb.AppendLine($"  {f.Name}: {arr.Length}");
                        if (f.Name.ToLower().Contains("system"))
                            foreach (var s in arr)
                            {
                                var t = s.GetType();
                                string Get(string n) => (t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(s) ?? t.GetProperty(n)?.GetValue(s))?.ToString() ?? "?";
                                sb.AppendLine($"     - name={Get("name")} type={Get("type")} capacity={Get("capacity")}");
                            }
                    }
                }
                var rep = graph.errorManager.compileReporter;
                foreach (var m in new VFXModel[] { graph }.Concat(graph.children).Concat(graph.children.OfType<VFXContext>().SelectMany(c => c.children)))
                    foreach (var e in rep.GetDirtyModelErrors(m)) sb.AppendLine($"  ERR [{m.GetType().Name} {m.name}] {e.type}: {e.error} {e.description}");
            }
            catch (Exception e) { sb.AppendLine("FAILED: " + e); }
            finally { setMode.Invoke(graph, new[] { oldMode }); Application.logMessageReceived -= cb; }
            sb.AppendLine("log warnings/errors:" + (logs.Count == 0 ? " none" : "\n  " + string.Join("\n  ", logs)));
            System.IO.File.WriteAllText(outFile, sb.ToString());
        };
        return "scheduled -> " + outFile;
    }

    /// Particle systems only compile when their data's owner list is ordered Init ... Update ... Output
    /// (VFXDataParticle.CanBeCompiled checks m_Owners[0] is Init and the last is Output); otherwise the system is
    /// silently skipped (compile "succeeds" with 0 systems). Reorders every particle data's owners and saves the asset.
    public static string FixOwnerOrder(string assetPath)
    {
        var graph = LoadGraph(assetPath);
        var sb = new StringBuilder();
        var ownersField = typeof(VFXData).GetField("m_Owners", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        int Rank(VFXContext c) => c.contextType == VFXContextType.Init ? 0 : c.contextType == VFXContextType.Update ? 1 : c.contextType == VFXContextType.Output ? 2 : 3;
        foreach (var data in graph.children.OfType<VFXContext>().Select(c => c.GetData()).OfType<VFXDataParticle>().Distinct())
        {
            var owners = (List<VFXContext>)ownersField.GetValue(data);
            string before = string.Join(", ", owners.Select(o => o.label));
            var sorted = owners.Select((o, i) => (o, i)).OrderBy(t => Rank(t.o)).ThenBy(t => t.i).Select(t => t.o).ToList();
            owners.Clear(); owners.AddRange(sorted);
            sb.AppendLine($"'{data.title}': [{before}] -> [{string.Join(", ", owners.Select(o => o.label))}] canCompile={data.CanBeCompiled()}");
            EditorUtility.SetDirty(data);
        }
        graph.SetExpressionGraphDirty();
        Save(graph);
        return sb.ToString();
    }

    /// Writes the graph back to its .vfx (same path the VFX Graph window's Save uses).
    internal static void Save(VFXGraph graph)
    {
        var ext = typeof(VFXGraph).Assembly.GetType("UnityEditor.VFX.VisualEffectResourceExtensions");
        var write = ext.GetMethod("WriteAssetWithSubAssets", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        write.Invoke(null, new object[] { graph.visualEffectResource });
    }

    // ---------- editing primitives ----------

    internal static VFXContext FindContext(VFXGraph g, VFXContextType type) => g.children.OfType<VFXContext>().First(c => c.contextType == type && c.GetData() is VFXDataParticle dp && dp.title == "Sparks" || c.contextType == type && type == VFXContextType.Spawner);
    internal static VFXBlock FindBlock(VFXContext c, string nameContains) => c.children.First(b => b.name.Contains(nameContains));
    /// "name" or a dotted sub-slot path, e.g. "arcSphere.sphere.center" (an unnamed top slot: ".center").
    internal static VFXSlot Input(IVFXSlotContainer m, string name)
    {
        var parts = name.Split('.');
        var slot = m.inputSlots.FirstOrDefault(s => s.property.name == parts[0])
            ?? throw new ArgumentException($"no input '{parts[0]}' on {m.GetType().Name}; inputs: [{string.Join(", ", m.inputSlots.Select(s => s.property.name))}]");
        for (int i = 1; i < parts.Length; i++)
        {
            var parent = slot;
            slot = parent.children.FirstOrDefault(s => s.property.name == parts[i])
                ?? throw new ArgumentException($"no sub-slot '{parts[i]}' in '{parent.property.name}'; sub-slots: [{string.Join(", ", parent.children.Select(s => s.property.name))}]");
        }
        return slot;
    }
    /// Blocks are switched on/off through their activation slot (that's what the block's toggle in the graph does).
    internal static void SetEnabled(VFXBlock b, bool on) => b.activationSlot.value = on;

    /// New exposed parameter of type T on the graph (returned so its output can be linked).
    internal static VFXParameter AddExposedParameter<T>(VFXGraph g, string name, T value)
    {
        // same as VFXLibrary's parameter descriptors / VFXPaste: CreateInstance + Init(type)
        var p = ScriptableObject.CreateInstance<VFXParameter>();
        p.Init(typeof(T));
        g.AddChild(p);
        p.SetSettingValue("m_ExposedName", name);
        p.SetSettingValue("m_Exposed", true);
        p.value = value;
        return p;
    }

    /// A Custom HLSL block runs ONE function of its code (the first by default). Selects it by name, like the block's
    /// function dropdown in the graph, and rebuilds the block's input slots from that function's parameters.
    internal static void SelectHlslFunction(UnityEditor.VFX.Block.CustomHLSL block, string functionName)
    {
        _ = block.inputSlots.Count;   // make sure the code has been parsed (fills the function list)
        block.ResyncSlots(true);
        var field = typeof(UnityEditor.VFX.Block.CustomHLSL).GetField("m_AvailableFunction", BindingFlags.Instance | BindingFlags.NonPublic);
        object choice = field.GetValue(block);
        choice.GetType().GetMethod("SetSelection").Invoke(choice, new object[] { functionName });
        field.SetValue(block, choice);
        block.Invalidate(VFXModel.InvalidationCause.kSettingChanged);
        block.ResyncSlots(true);
    }

    static string FlowList(VFXContextSlot[] slots, Func<VFXModel, string> id)
    {
        if (slots == null || slots.Length == 0) return "[]";
        return "[" + string.Join(" | ", slots.Select((s, i) => i + ":" + string.Join(",", s.link.Select(l => id(l.context) + "." + l.slotIndex)))) + "]";
    }

    static string SubgraphDescription(VFXModel m)
    {
        UnityEngine.Object subgraph;
        if (m is VFXSubgraphOperator op) subgraph = op.subgraph;
        else if (m is VFXSubgraphBlock block) subgraph = block.subgraph;
        else if (m is VFXSubgraphContext context) subgraph = context.subgraph;
        else return "";
        return $" m_Subgraph={(subgraph == null ? "(null)" : AssetDatabase.GetAssetPath(subgraph))}";
    }

    static void AppendSettings(StringBuilder sb, VFXModel m, string indent)
    {
        var settings = m.GetSettings(false).ToList();
        if (settings.Count == 0) return;
        sb.AppendLine(indent + "settings: " + string.Join(", ", settings.Select(s => s.name + "=" + Short(s.value))));
    }

    static void AppendInputs(StringBuilder sb, IVFXSlotContainer m, Func<VFXModel, string> id, string indent)
    {
        foreach (var s in m.inputSlots)
        {
            string link = InputLink(s, id);
            sb.AppendLine($"{indent}in  {s.property.name} : {s.property.type?.Name} = {Short(s.value)}{link}");
        }
    }

    // "<- O<n>.<slot>" for an input slot's incoming link(s) (empty when nothing is wired).
    static string InputLink(VFXSlot s, Func<VFXModel, string> id)
    {
        if (s == null || !s.HasLink(true)) return "";
        return "  <- " + string.Join(", ", AllLinks(s).Select(l => id(OwnerModel(l)) + "." + l.path));
    }

    static void AppendLinks(StringBuilder sb, VFXSlot s, Func<VFXModel, string> id, string label)
    {
        var links = AllLinks(s).ToList();
        if (links.Count > 0) sb.AppendLine($"    {label} {s.path} -> " + string.Join(", ", links.Select(l => id(OwnerModel(l)) + "." + l.path)));
    }

    static IEnumerable<VFXSlot> AllLinks(VFXSlot s)
    {
        foreach (var l in s.LinkedSlots) yield return l;
        foreach (var c in s.children) foreach (var l in AllLinks(c)) yield return l;
    }

    static VFXModel OwnerModel(VFXSlot s) => s.owner as VFXModel;

    static void AppendErrors(StringBuilder sb, VFXModel m, string indent)
    {
        try
        {
            var rep = new VFXErrorReporter(VFXErrorOrigin.Invalidate);
            m.GenerateErrors(rep);
            foreach (var e in rep.GetDirtyModelErrors(m))
                sb.AppendLine($"{indent}!! {e.type}: {e.error} {e.description}");
        }
        catch (Exception e) { sb.AppendLine($"{indent}!! (error check failed: {e.GetType().Name})"); }
    }

    static string Short(object v)
    {
        if (v == null) return "null";
        if (v is UnityEngine.Object o) return o ? $"{o.GetType().Name}'{o.name}'({AssetDatabase.GetAssetPath(o)})" : "None";
        var s = v is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : v.ToString();
        s = s.Replace("\n", " ");
        return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }
}
