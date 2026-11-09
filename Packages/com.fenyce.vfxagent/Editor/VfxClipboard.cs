// Model-level clipboard, slices A1+A2: operators, parameters, contexts and blocks.
// Copy/Paste/PasteBlocks are assembly-internal because the VFX model types in their signatures are internal.
// Supported: concrete settings, top-level input values/spaces, parameter metadata/defaults, node UI state,
// ordered/enabled context blocks, VFXData settings, internal data and flow links, and incoming external
// data links for same-graph paste. A selected parameter copies all its visual nodes. Paste returns only
// top-level records (not the blocks nested in a copied context). offset is added to every position.
// Cross-graph parameters reuse an existing same-name (case-insensitive), same-type parameter, or are
// created with a unique name; reused parameter metadata/defaults are left unchanged. External incoming
// links are dropped across graphs; outgoing boundary links are never copied. A blocks-only snapshot
// copies blocks from one source context, in source order, and PasteBlocks appends them to a compatible
// target context. Blocks-only snapshots deliberately have no data edges, like stock VFXCopy.CopyBlocks.
// Custom HLSL blocks and subgraph wrappers copy their serialized settings/asset references as-is; no
// HLSL files or subgraph contents are copied. Groups, sticky notes and custom HLSL operators are unsupported.
// Stock references: VFXCopy.cs:291-428 (nodes/contexts/blocks/data), VFXPaste.cs:194-250 (block paste),
// VFXPaste.cs:374-425 (context creation), VFXPaste.cs:724-765 (data before flow), and
// VFXContext.cs:427-465,598-620 (flow propagation shares the upstream VFXData downstream).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.VFX;
using UnityEditor.VFX.UI;
using UnityEngine;
using UnityEngine.VFX;
public static class VfxClipboard
{
    internal sealed class Snapshot
    {
        internal VFXGraph source;
        internal bool blocksOnly;
        internal readonly List<NodeData> nodes = new List<NodeData>();
        internal readonly List<DataData> datas = new List<DataData>();
        internal readonly List<EdgeData> edges = new List<EdgeData>();
        internal readonly List<FlowData> flowEdges = new List<FlowData>();
    }
    internal sealed class NodeData
    {
        internal SerializableType type;
        internal ParameterData parameter;
        internal int parent = -1; // Flat index of the parent context for a block; -1 for a top-level record.
        internal int dataIndex = -1;
        internal string label, systemName;
        internal readonly List<SubOutputData> subOutputs = new List<SubOutputData>();
        internal Vector2 position;
        internal bool collapsed, superCollapsed, activation;
        internal List<PropertyData> settings = new List<PropertyData>(), inputs = new List<PropertyData>();
    }
    internal sealed class DataData
    {
        internal List<PropertyData> settings = new List<PropertyData>();
    }
    internal sealed class SubOutputData
    {
        internal SerializableType type;
        internal List<PropertyData> settings = new List<PropertyData>();
    }
    internal sealed class PropertyData
    {
        internal string name;
        internal ValueData value;
        internal VFXSpace space;
    }
    internal struct ValueData
    {
        internal VFXSerializer.TypedSerializedData data;
        internal static ValueData Capture(object value) => new ValueData { data = value == null ? VFXSerializer.TypedSerializedData.Null : VFXSerializer.SaveWithType(value) };
        internal object Get() => VFXSerializer.LoadWithType(data, null);
    }
    internal sealed class ParameterData
    {
        internal EntityId id;
        internal SerializableType type;
        internal string name, category, tooltip;
        internal int order;
        internal bool exposed, isOutput, collapsed;
        internal ValueData value, min, max;
        internal VFXValueFilter filter;
        internal string[] enumValues;
        internal VFXSpace space;
        internal readonly List<VisualData> visuals = new List<VisualData>();
    }
    internal sealed class VisualData
    {
        internal Vector2 position;
        internal bool expanded, superCollapsed;
        internal string[] expandedSlots;
    }
    internal sealed class EdgeData
    {
        internal int input, output, inputVisual, outputVisual;
        internal int[] inputPath, outputPath;
        internal VFXSlot externalOutput;
        internal VFXParameter.Node externalVisual; // Same-graph paste only.
    }
    internal sealed class FlowData
    {
        internal int input, output, inputFlow, outputFlow;
    }

    internal static Snapshot Copy(VFXGraph g, IEnumerable<VFXModel> models)
    {
        if (g == null) throw new ArgumentNullException(nameof(g));
        if (models == null) throw new ArgumentNullException(nameof(models));
        var requested = models.Distinct().ToList();
        if (requested.Count == 0) throw new ArgumentException("The clipboard selection is empty.", nameof(models));
        foreach (var model in requested)
        {
            if (model == null) throw new ArgumentException("The clipboard selection contains null.", nameof(models));
            if (model.GetGraph() != g) throw new ArgumentException($"Model '{model.name}' does not belong to the supplied graph.", nameof(models));
            if (model is UnityEditor.VFX.Operator.CustomHLSL)
                throw new NotSupportedException("Custom HLSL operators are not supported by this clipboard slice.");
        }

        bool blocksOnly = requested.All(m => m is VFXBlock);
        if (!blocksOnly && requested.Any(m => m is VFXBlock))
            throw new NotSupportedException("Blocks must be copied alone; do not mix B: items with contexts, operators or parameters.");

        var selected = new List<VFXModel>();
        var parents = new List<int>();
        if (blocksOnly)
        {
            var sourceContext = ((VFXBlock)requested[0]).GetParent();
            if (sourceContext == null || requested.Any(m => ((VFXBlock)m).GetParent() != sourceContext))
                throw new NotSupportedException("A blocks-only copy must come from one source context.");
            // The window's block paste preserves the copied sequence. Use model order, not selector order.
            selected = requested.OrderBy(m => sourceContext.GetIndex(m)).ToList();
            parents.AddRange(Enumerable.Repeat(-1, selected.Count));
        }
        else
        {
            foreach (var model in requested)
            {
                if (model is VFXBlockSubgraphContext)
                    throw new NotSupportedException("VFXBlockSubgraphContext cannot be copied (matching stock VFXCopy).");
                if (!(model is VFXContext) && !(model is VFXOperator) && !(model is VFXParameter))
                    throw new NotSupportedException($"Unsupported {model.GetType().Name}: expected a context, operator or parameter.");
                int parent = selected.Count;
                selected.Add(model);
                parents.Add(-1);
                if (model is VFXContext context)
                    foreach (var block in context.children)
                    {
                        selected.Add(block);
                        parents.Add(parent);
                    }
            }
        }

        var ids = selected.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);
        var snapshot = new Snapshot { source = g, blocksOnly = blocksOnly };
        var dataIds = new Dictionary<VFXData, int>();
        for (int i = 0; i < selected.Count; ++i)
            snapshot.nodes.Add(CaptureNode(selected[i], parents[i], snapshot, dataIds));

        if (!blocksOnly)
        {
            CaptureDataEdges(snapshot, selected, ids);
            CaptureFlowEdges(snapshot, selected, ids);
        }
        return snapshot;
    }

    internal static List<VFXModel> Paste(VFXGraph target, Snapshot s, Vector2 offset)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (s == null) throw new ArgumentNullException(nameof(s));
        if (s.blocksOnly) throw new InvalidOperationException("A blocks-only clipboard requires PasteBlocks / 'clippaste into <ctx>'.");
        bool sameGraph = ReferenceEquals(target, s.source);
        var map = new IVFXSlotContainer[s.nodes.Count];
        var visuals = new VFXParameter.Node[s.nodes.Count][];
        var result = new List<VFXModel>();

        // Create all top-level models first. Context creation through graph.AddChild gives each context
        // its default VFXData, matching VFXPaste.PasteAndInitializeNode (VFXPaste.cs:407-425).
        for (int i = 0; i < s.nodes.Count; ++i)
        {
            var data = s.nodes[i];
            if (data.parent >= 0) continue;
            if (data.parameter != null)
            {
                var p = RestoreParameter(target, data.parameter, sameGraph);
                map[i] = p;
                result.Add(p);
                visuals[i] = new VFXParameter.Node[data.parameter.visuals.Count];
                for (int j = 0; j < visuals[i].Length; ++j)
                {
                    var source = data.parameter.visuals[j];
                    int id = p.AddNode(source.position + offset);
                    var node = p.GetNode(id);
                    node.expanded = source.expanded;
                    node.supecollapsed = source.superCollapsed;
                    var slots = Stock.Slots(p.isOutput ? p.inputSlots : p.outputSlots);
                    node.expandedSlots = slots.Where(x => source.expandedSlots.Contains(x.path)).ToList();
                    visuals[i][j] = node;
                }
                continue;
            }

            var type = (Type)data.type;
            if (type == null || type.IsAbstract) throw new InvalidOperationException("The snapshot contains an unavailable node type.");
            if (typeof(VFXContext).IsAssignableFrom(type))
            {
                var context = (VFXContext)ScriptableObject.CreateInstance(type);
                target.AddChild(context);
                RestoreNode(context, data, offset);
                context.label = data.label;
                VFXSystemNames.SetSystemName(context, data.systemName);
                RestoreSubOutputs(context, data.subOutputs);
                map[i] = context;
                result.Add(context);
            }
            else if (typeof(VFXOperator).IsAssignableFrom(type))
            {
                var op = (VFXOperator)ScriptableObject.CreateInstance(type);
                target.AddChild(op);
                RestoreNode(op, data, offset);
                map[i] = op;
                result.Add(op);
            }
            else throw new InvalidOperationException($"Unsupported top-level clipboard type '{type.Name}'.");
        }

        // Restore blocks in their captured order, after their owning context exists. AddChild performs
        // the same compatibility check as the window; make failures explicit instead of silently skipping.
        for (int i = 0; i < s.nodes.Count; ++i)
        {
            var data = s.nodes[i];
            if (data.parent < 0) continue;
            var parent = map[data.parent] as VFXContext
                ?? throw new InvalidOperationException("A copied block has no restored parent context.");
            var block = RestoreBlock(data, offset);
            if (!parent.AcceptChild(block))
                throw new InvalidOperationException($"Block '{block.name}' ({block.GetType().Name}) is incompatible with copied context '{parent.name}'.");
            parent.AddChild(block);
            AfterBlockAttached(block, data);
            map[i] = block;
        }

        RestoreDataSettings(s, map);
        RestoreDataEdges(s, map, visuals, sameGraph);

        // VFXPaste restores flow after data. LinkFrom/InnerLink propagates the upstream VFXData through
        // each copied particle system, so contexts that shared data in the source share it again.
        foreach (var edge in s.flowEdges)
        {
            var input = map[edge.input] as VFXContext;
            var output = map[edge.output] as VFXContext;
            if (input == null || output == null) throw new InvalidOperationException("A clipboard flow link has no restored context endpoint.");
            input.LinkFrom(output, edge.outputFlow, edge.inputFlow);
        }
        target.Invalidate(VFXModel.InvalidationCause.kStructureChanged);
        return result;
    }

    internal static List<VFXBlock> PasteBlocks(VFXContext target, Snapshot s)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (s == null) throw new ArgumentNullException(nameof(s));
        if (!s.blocksOnly) throw new InvalidOperationException("'clippaste into' requires a blocks-only clipboard made with B: items.");

        // Preflight every block before changing the target. Stock VFXPaste uses AcceptChild before
        // AddChild (VFXPaste.cs:194-219); unlike stock, an incompatible block is an error, never skipped.
        var blocks = new List<VFXBlock>(s.nodes.Count);
        foreach (var data in s.nodes)
        {
            var block = RestoreBlock(data, Vector2.zero);
            if (!target.AcceptChild(block))
                throw new InvalidOperationException($"Block '{block.name}' ({block.GetType().Name}) is incompatible with target context '{target.name}' ({target.GetType().Name}).");
            blocks.Add(block);
        }
        for (int i = 0; i < blocks.Count; ++i)
        {
            target.AddChild(blocks[i], -1, false); // Append sequentially; -1 keeps the captured order.
            AfterBlockAttached(blocks[i], s.nodes[i]);
        }
        target.Invalidate(VFXModel.InvalidationCause.kStructureChanged);
        return blocks;
    }

    static void CaptureDataEdges(Snapshot snapshot, List<VFXModel> selected, Dictionary<VFXModel, int> ids)
    {
        for (int i = 0; i < selected.Count; ++i)
        {
            var container = (IVFXSlotContainer)selected[i];
            var inputs = Stock.Slots(container.inputSlots);
            if (container.activationSlot != null) inputs = inputs.Append(container.activationSlot);
            foreach (var input in inputs)
                foreach (var output in input.LinkedSlots.ToArray())
                {
                    var owner = output.owner as VFXModel;
                    int outputIndex = -1; // -1 means the output lies outside the copied selection.
                    bool inside = owner != null && ids.TryGetValue(owner, out outputIndex);
                    if (!inside) outputIndex = -1;
                    var parameter = selected[i] as VFXParameter;
                    var externalParameter = inside ? null : owner as VFXParameter;
                    int externalIndex = VisualIndex(externalParameter, output, input);
                    snapshot.edges.Add(new EdgeData
                    {
                        input = i,
                        output = outputIndex,
                        inputPath = SlotPath(input, true),
                        outputPath = inside ? SlotPath(output, false) : null,
                        inputVisual = VisualIndex(parameter, output, input),
                        outputVisual = inside ? VisualIndex(owner as VFXParameter, output, input) : -1,
                        externalOutput = inside ? null : output,
                        externalVisual = externalParameter != null && externalIndex >= 0 && externalIndex < externalParameter.nodes.Count ? externalParameter.nodes[externalIndex] : null
                    });
                }
        }
    }

    static void CaptureFlowEdges(Snapshot snapshot, List<VFXModel> selected, Dictionary<VFXModel, int> ids)
    {
        for (int i = 0; i < selected.Count; ++i)
        {
            if (!(selected[i] is VFXContext output)) continue;
            for (int outputFlow = 0; outputFlow < output.outputFlowSlot.Length; ++outputFlow)
                foreach (var link in output.outputFlowSlot[outputFlow].link)
                    if (link.context != null && ids.TryGetValue(link.context, out int input))
                        snapshot.flowEdges.Add(new FlowData
                        {
                            input = input,
                            output = i,
                            inputFlow = link.slotIndex,
                            outputFlow = outputFlow
                        });
        }
    }

    static NodeData CaptureNode(VFXModel model, int parent, Snapshot snapshot, Dictionary<VFXData, int> dataIds)
    {
        if (model is VFXParameter parameter) return new NodeData { parameter = CaptureParameter(parameter), parent = parent };
        var container = (IVFXSlotContainer)model;
        var data = new NodeData
        {
            type = model.GetType(),
            parent = parent,
            position = model.position,
            collapsed = model.collapsed,
            superCollapsed = model.superCollapsed,
            activation = container.activationSlot != null && (bool)container.activationSlot.value,
            settings = CaptureSettings(model),
            inputs = container.inputSlots.Select(x => new PropertyData { name = x.name, value = ValueData.Capture(x.value), space = x.spaceable ? x.space : VFXSpace.None }).ToList()
        };
        if (model is VFXContext context)
        {
            data.label = context.label;
            data.systemName = VFXSystemNames.GetSystemName(context);
            var contextData = context.GetData();
            if (contextData != null)
            {
                if (!dataIds.TryGetValue(contextData, out int dataIndex))
                {
                    dataIndex = snapshot.datas.Count;
                    dataIds.Add(contextData, dataIndex);
                    snapshot.datas.Add(new DataData { settings = CaptureSettings(contextData) });
                }
                data.dataIndex = dataIndex;
            }
            if (context is VFXAbstractRenderedOutput output)
                data.subOutputs.AddRange(output.GetSubOutputs().Where(x => x != null).Select(x => new SubOutputData { type = x.GetType(), settings = CaptureSettings(x) }));
        }
        return data;
    }

    static List<PropertyData> CaptureSettings(VFXModel model) =>
        Stock.Fields(model.GetType()).Select(f => new PropertyData { name = f.Name, value = ValueData.Capture(f.GetValue(model)) }).ToList();

    static ParameterData CaptureParameter(VFXParameter p)
    {
        var slot = p.isOutput ? p.inputSlots[0] : p.outputSlots[0];
        var data = new ParameterData
        {
            id = p.GetEntityId(),
            type = p.type,
            name = p.exposedName,
            category = p.category,
            tooltip = p.tooltip,
            order = p.order,
            exposed = p.exposed,
            isOutput = p.isOutput,
            collapsed = p.collapsed,
            value = ValueData.Capture(p.value),
            filter = p.valueFilter,
            min = p.valueFilter == VFXValueFilter.Range ? ValueData.Capture(p.min) : default(ValueData),
            max = p.valueFilter == VFXValueFilter.Range ? ValueData.Capture(p.max) : default(ValueData),
            enumValues = p.valueFilter == VFXValueFilter.Enum ? p.enumValues?.ToArray() : null,
            space = slot.spaceable ? slot.space : VFXSpace.None
        };
        if (p.nodes.Count == 0)
            data.visuals.Add(new VisualData { position = p.position, expanded = true, expandedSlots = Stock.Slots(slot).Where(x => !x.collapsed).Select(x => x.path).ToArray() });
        else
            data.visuals.AddRange(p.nodes.Select(n => new VisualData
            {
                position = n.position,
                expanded = n.expanded,
                superCollapsed = n.supecollapsed,
                expandedSlots = n.expandedSlots?.Select(x => x.path).ToArray() ?? Array.Empty<string>()
            }));
        return data;
    }

    static VFXParameter RestoreParameter(VFXGraph target, ParameterData data, bool sameGraph)
    {
        var type = (Type)data.type;
        if (type == null) throw new InvalidOperationException($"The type of parameter '{data.name}' is unavailable.");
        var parameters = target.children.OfType<VFXParameter>();
        var p = sameGraph
            ? parameters.FirstOrDefault(x => x.GetEntityId() == data.id)
            : parameters.FirstOrDefault(x => x.type == type && string.Equals(x.exposedName, data.name, StringComparison.OrdinalIgnoreCase));
        if (p == null)
        {
            string copyName = UniqueName(target, data.name);
            p = ScriptableObject.CreateInstance<VFXParameter>();
            p.Init(type);
            target.AddChild(p);
            p.isOutput = data.isOutput;
            p.SetSettingValue("m_ExposedName", copyName);
            p.SetSettingValue("m_Exposed", data.exposed);
            p.order = data.order;
            p.category = data.category;
            p.tooltip = data.tooltip;
            p.collapsed = data.collapsed;
            if (!p.isOutput) p.value = data.value.Get();
            p.valueFilter = data.filter;
            if (data.filter == VFXValueFilter.Range) { p.min = data.min.Get(); p.max = data.max.Get(); }
            else if (data.filter == VFXValueFilter.Enum) p.enumValues = data.enumValues?.ToList() ?? new List<string>();
        }
        else if (p.type != type || p.isOutput != data.isOutput)
            throw new InvalidOperationException($"Reused parameter '{p.exposedName}' changed type or direction since it was copied.");
        var slot = p.isOutput ? p.inputSlots[0] : p.outputSlots[0];
        if (slot.spaceable) slot.space = data.space;
        return p;
    }

    static VFXBlock RestoreBlock(NodeData data, Vector2 offset)
    {
        var type = (Type)data.type;
        if (type == null || !typeof(VFXBlock).IsAssignableFrom(type) || type.IsAbstract)
            throw new InvalidOperationException("The snapshot contains an unavailable block type.");
        var block = (VFXBlock)ScriptableObject.CreateInstance(type);
        RestoreNode(block, data, offset);
        return block;
    }

    static void RestoreNode(VFXModel model, NodeData data, Vector2 offset)
    {
        RestoreSettings(model, data.settings);
        model.Invalidate(VFXModel.InvalidationCause.kSettingChanged); // Resyncs settings-dependent slots.
        model.position = data.position + offset;
        model.collapsed = data.collapsed;
        model.superCollapsed = data.superCollapsed;
        var container = (IVFXSlotContainer)model;
        if (container.activationSlot != null) container.activationSlot.value = data.activation;
        RestoreInputs(container, data.inputs);
    }

    static void RestoreInputs(IVFXSlotContainer container, List<PropertyData> inputs)
    {
        for (int j = 0; j < inputs.Count && j < container.inputSlots.Count; ++j)
            if (container.inputSlots[j].name == inputs[j].name)
            {
                container.inputSlots[j].value = inputs[j].value.Get();
                if (container.inputSlots[j].spaceable) container.inputSlots[j].space = inputs[j].space;
            }
    }

    static void AfterBlockAttached(VFXBlock block, NodeData data)
    {
        // A subgraph wrapper's m_Subgraph is just a serialized setting, but its slots can only be rebuilt
        // after attachment. Custom HLSL likewise reparses in OnAdded; reapply values after that resync,
        // matching VFXPaste.CopyCustomHLSLInputProperties (VFXPaste.cs:204-210,236-250).
        if (block is VFXSubgraphBlock) block.ResyncSlots(true);
        if (block is VFXSubgraphBlock || block is UnityEditor.VFX.Block.CustomHLSL)
            RestoreInputs(block, data.inputs);
    }

    static void RestoreSettings(VFXModel model, List<PropertyData> settings)
    {
        var fields = Stock.Fields(model.GetType());
        foreach (var setting in settings)
            try { fields.FirstOrDefault(x => x.Name == setting.name)?.SetValue(model, setting.value.Get()); }
            catch { } // Match VFXPaste: one incompatible setting must not abort the paste.
    }

    static void RestoreSubOutputs(VFXContext context, List<SubOutputData> subOutputs)
    {
        if (!(context is VFXAbstractRenderedOutput output)) return;
        var restored = new List<VFXSRPSubOutput>(subOutputs.Count);
        foreach (var data in subOutputs)
        {
            var type = (Type)data.type;
            if (type == null || !typeof(VFXSRPSubOutput).IsAssignableFrom(type) || type.IsAbstract) continue;
            var subOutput = (VFXSRPSubOutput)ScriptableObject.CreateInstance(type);
            RestoreSettings(subOutput, data.settings);
            restored.Add(subOutput);
        }
        output.InitSubOutputs(restored);
    }

    static void RestoreDataSettings(Snapshot s, IVFXSlotContainer[] map)
    {
        var contexts = s.nodes.Select((node, index) => (node, index))
            .Where(x => x.node.parent < 0 && x.node.dataIndex >= 0 && map[x.index] is VFXContext)
            .GroupBy(x => x.node.dataIndex);
        foreach (var group in contexts)
        {
            // Apply each source data record at a copied flow root. Subsequent flow restoration propagates
            // that one data object (including capacity and other settings) to all selected downstream owners.
            var roots = group.Where(x => !s.flowEdges.Any(e => e.input == x.index)).ToList();
            if (roots.Count == 0) roots = group.ToList();
            foreach (var item in roots)
            {
                var data = ((VFXContext)map[item.index]).GetData();
                if (data == null) continue;
                RestoreSettings(data, s.datas[item.node.dataIndex].settings);
                data.Invalidate(VFXModel.InvalidationCause.kSettingChanged);
            }
        }
    }

    static void RestoreDataEdges(Snapshot s, IVFXSlotContainer[] map, VFXParameter.Node[][] visuals, bool sameGraph)
    {
        foreach (var edge in s.edges)
        {
            if (edge.output < 0 && !sameGraph) continue;
            var input = FetchSlot(map[edge.input], edge.inputPath, true);
            var output = edge.output >= 0 ? FetchSlot(map[edge.output], edge.outputPath, false) : edge.externalOutput;
            if (input == null || output == null || !input.Link(output))
                throw new InvalidOperationException("A clipboard data link could not be restored (slot structure or type changed).");
            Remember(VisualAt(visuals, edge.input, edge.inputVisual), output, input);
            Remember(edge.output >= 0 ? VisualAt(visuals, edge.output, edge.outputVisual) : edge.externalVisual, output, input);
        }
    }

    static string UniqueName(VFXGraph graph, string wanted)
    {
        var names = new HashSet<string>(graph.children.OfType<VFXParameter>().Select(x => x.exposedName), StringComparer.OrdinalIgnoreCase);
        return VFXParameterController.MakeNameUnique(wanted, names, true, VFXParameterController.kMaxExposedNameLength);
    }

    static int VisualIndex(VFXParameter p, VFXSlot output, VFXSlot input)
    {
        if (p == null) return -1;
        for (int i = 0; i < p.nodes.Count; ++i)
            if (p.nodes[i].linkedSlots?.Any(x => x.outputSlot == output && x.inputSlot == input) == true) return i;
        return 0;
    }

    static VFXParameter.Node VisualAt(VFXParameter.Node[][] visuals, int record, int index) =>
        record >= 0 && index >= 0 && visuals[record] != null && index < visuals[record].Length ? visuals[record][index] : null;

    static void Remember(VFXParameter.Node node, VFXSlot output, VFXSlot input)
    {
        if (node == null) return;
        node.linkedSlots = node.linkedSlots ?? new List<VFXParameter.NodeLinkedSlot>();
        if (!node.linkedSlots.Any(x => x.outputSlot == output && x.inputSlot == input))
            node.linkedSlots.Add(new VFXParameter.NodeLinkedSlot { outputSlot = output, inputSlot = input });
    }

    static int[] SlotPath(VFXSlot slot, bool input)
    {
        var path = new List<int>();
        while (slot.GetParent() != null) { path.Add(slot.GetParent().GetIndex(slot)); slot = slot.GetParent(); }
        path.Add(ReferenceEquals(slot, slot.owner.activationSlot) ? -2 : (input ? slot.owner.inputSlots : slot.owner.outputSlots).IndexOf(slot));
        return path.ToArray();
    }

    static VFXSlot FetchSlot(IVFXSlotContainer container, int[] path, bool input)
    {
        if (container == null || path == null || path.Length == 0) return null;
        int index = path[path.Length - 1];
        var slot = index == -2 ? container.activationSlot : input ? container.GetInputSlot(index) : container.GetOutputSlot(index);
        for (int i = path.Length - 2; slot != null && i >= 0; --i)
            slot = slot.GetNbChildren() > path[i] ? slot[path[i]] : null;
        return slot;
    }

    // Reuse the stock field filtering and recursive-slot walk, without controllers or GraphView objects.
    internal sealed class Stock : VFXCopyPasteCommon
    {
        internal static List<FieldInfo> Fields(Type type) => GetFields(type);
        internal static IEnumerable<VFXSlot> Slots(IEnumerable<VFXSlot> slots) => AllSlots(slots);
        internal static IEnumerable<VFXSlot> Slots(VFXSlot slot) => AllSlots(new[] { slot });
    }
}
