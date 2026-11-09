// Builds the VFX Agent demo from the op-scripts, without hand-editing any graph.
// Editor-only: it runs every Scripts/*.vfxscript through VfxGraphOps, then regenerates
// Assets/VfxAgentDemo/Demo.unity with one effect per generated .vfx. It never enters Play mode.

using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;

public static class VfxAgentDemoBuilder
{
    const string Root = "Assets/VfxAgentDemo";
    const string ScriptsFolder = Root + "/Scripts";
    const string EffectsFolder = Root + "/Effects";
    const string ScenePath = Root + "/Demo.unity";
    const string LogPath = Root + "/BuildLog.txt";

    // Menu entry: the log line it prints is the one-line summary.
    [MenuItem("Tools/VFX Agent/Build Demo")]
    public static void BuildDemoMenu()
    {
        Debug.Log(BuildAll());
    }

    /// <summary>
    /// Runs all op-scripts in name order, rebuilds Demo.unity, writes BuildLog.txt and
    /// returns "VFX Agent demo: N scripts, M ERROR lines -> Assets/VfxAgentDemo/BuildLog.txt".
    /// </summary>
    public static string BuildAll()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("The VFX Agent demo builder must run in Edit mode, not Play mode.");

        var log = new StringBuilder();
        var scripts = new string[0];
        int errorLines;

        try
        {
            log.AppendLine("# VFX Agent demo build");
            log.AppendLine("# Unity " + Application.unityVersion + " - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // (a) make sure the output folder exists.
            EnsureFolder(Root);
            EnsureFolder(EffectsFolder);

            // Effects/ is generated. Clear the old subgraph assets so reruns work: `newsubgraph`
            // refuses to overwrite an existing .vfxoperator. The .vfx outputs are replaced by
            // each script's `copy`, so nothing else has to be deleted.
            RemoveGeneratedSubgraphs(log);

            // (b) every script, in name order.
            scripts = Directory.Exists(AbsolutePath(ScriptsFolder))
                ? Directory.GetFiles(AbsolutePath(ScriptsFolder), "*.vfxscript")
                    .Where(p => p.EndsWith(".vfxscript", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : new string[0];

            foreach (var script in scripts)
            {
                log.AppendLine();
                log.AppendLine("=== " + Path.GetFileName(script) + " ===");
                try { log.AppendLine(VfxGraphOps.Apply(File.ReadAllText(script)).TrimEnd()); }
                catch (Exception e) { log.AppendLine("ERROR " + e.GetType().Name + ": " + e.Message); }
            }

            AssetDatabase.Refresh();

            // (c) Demo.unity: camera + light + one VisualEffect per generated .vfx.
            log.AppendLine();
            log.AppendLine("=== scene ===");
            BuildScene(log);
        }
        catch (Exception e)
        {
            log.AppendLine("ERROR " + e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            // (d) always leave the log behind, even if the build failed early.
            errorLines = log.ToString().Split('\n').Count(l => l.Contains("ERROR"));
            File.WriteAllText(AbsolutePath(LogPath), log.ToString());
            AssetDatabase.ImportAsset(LogPath, ImportAssetOptions.ForceUpdate);
        }

        return "VFX Agent demo: " + scripts.Length + " scripts, " + errorLines + " ERROR lines -> " + LogPath;
    }

    static void BuildScene(StringBuilder log)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Camera at (0, 3, -14), aimed at the origin (where the effect row is centred).
        var cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        cameraGo.AddComponent<Camera>().fieldOfView = 50f;
        cameraGo.transform.position = new Vector3(0f, 3f, -14f);
        cameraGo.transform.LookAt(Vector3.zero);

        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var effectPaths = Directory.GetFiles(AbsolutePath(EffectsFolder))
            .Where(p => p.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))   // not .vfxoperator
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 4 m apart on X, centred on the origin.
        float startX = -(effectPaths.Count - 1) * 2f;
        for (int i = 0; i < effectPaths.Count; i++)
        {
            string fileName = Path.GetFileName(effectPaths[i]);
            string assetPath = EffectsFolder + "/" + fileName;
            string label = Path.GetFileNameWithoutExtension(fileName);

            var go = new GameObject(label);
            go.transform.position = new Vector3(startX + i * 4f, 0f, 0f);

            var vfx = go.AddComponent<VisualEffect>();
            var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
            if (asset != null) vfx.visualEffectAsset = asset;
            else log.AppendLine("ERROR could not load " + assetPath);

            AddLabel(go, label);
            log.AppendLine("# " + label + " at " + go.transform.position);
        }

        bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
        log.AppendLine("# scene " + (saved ? "saved" : "NOT saved") + ": " + ScenePath);
        if (!saved) log.AppendLine("ERROR could not save scene " + ScenePath);
    }

    // A simple 3D Text label (legacy TextMesh, no extra packages) under the effect.
    static void AddLabel(GameObject owner, string text)
    {
        var labelGo = new GameObject("Label " + text);
        labelGo.transform.SetParent(owner.transform, false);
        labelGo.transform.localPosition = new Vector3(0f, -1.5f, 0f);
        labelGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);   // face the camera on -Z

        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var mesh = labelGo.AddComponent<TextMesh>();
        mesh.text = text;
        mesh.font = font;
        mesh.fontSize = 64;
        mesh.characterSize = 0.1f;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.color = Color.white;

        var renderer = labelGo.GetComponent<MeshRenderer>();
        if (renderer == null) renderer = labelGo.AddComponent<MeshRenderer>();
        if (font != null) renderer.sharedMaterial = font.material;
    }

    // Subgraph assets cannot be overwritten by `newsubgraph`, so drop the previous run's.
    static void RemoveGeneratedSubgraphs(StringBuilder log)
    {
        if (!AssetDatabase.IsValidFolder(EffectsFolder)) return;
        foreach (var file in Directory.GetFiles(AbsolutePath(EffectsFolder)))
        {
            bool subgraph = file.EndsWith(".vfxoperator", StringComparison.OrdinalIgnoreCase)
                         || file.EndsWith(".vfxblock", StringComparison.OrdinalIgnoreCase);
            if (!subgraph) continue;

            string assetPath = EffectsFolder + "/" + Path.GetFileName(file);
            if (AssetDatabase.DeleteAsset(assetPath)) log.AppendLine("# removed stale " + assetPath);
        }
    }

    static void EnsureFolder(string assetFolder)
    {
        if (AssetDatabase.IsValidFolder(assetFolder)) return;
        string parent = Path.GetDirectoryName(assetFolder);
        if (string.IsNullOrEmpty(parent)) return;
        parent = parent.Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
    }

    static string AbsolutePath(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
    }
}
