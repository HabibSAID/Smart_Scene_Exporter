// Assets/Editor/SmartSceneExporter.cs
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class SmartSceneExporter : EditorWindow
{
    public enum ExportMode
    {
        VisualsOnly = 0,            // Professional: visuals/assets only
        VisualsAndAnimations = 1,   // Professional: visuals + animation data
        CompleteProject = 2         // Professional: visuals + animations + code
    }

    private enum AssetKind { Scene, Code, Animation, Other }

    [MenuItem("Tools/Smart Scene Exporter")]
    public static void OpenWindow()
    {
        var w = GetWindow<SmartSceneExporter>();
        w.titleContent = new GUIContent("Smart Scene Exporter");
        w.minSize = new Vector2(980, 640);
        w.Show();
        w.Focus();
    }

    [SerializeField] private SceneAsset sceneAsset;
    [SerializeField] private ExportMode exportMode = ExportMode.VisualsAndAnimations;

    [Header("Exclusions")]
    [SerializeField] private bool excludePluginsFolder = false;
    [SerializeField] private bool excludeEditorFolders = false;
    [SerializeField] private bool excludeAddressablesData = false;
    [SerializeField] private bool excludeStreamingAssets = false;

    [Header("Extra Include")]
    [SerializeField] private UnityEngine.Object[] extraInclude = Array.Empty<UnityEngine.Object>();

    [Header("Organize Export (Staging)")]
    [SerializeField] private bool reorganizeIntoFolders = false;
    [SerializeField] private string stagingRootFolderName = "__SceneExport_Staging";
    [SerializeField] private bool deleteStagingAfterExport = true;

    // Preview UI state (simple + stable)
    private Vector2 _scroll;
    private string _search = "";
    private bool _showOnlySelected = false;

    private bool _autoRefresh = true;

    // Data
    private List<string> _computedPaths = new();
    private Dictionary<string, bool> _selected = new();
    private Dictionary<string, AssetKind> _kind = new();
    private string _lastFingerprint = "";
    private string _lastComputeNote = "";

    private void OnEnable()
    {
        RecomputeIfNeeded(true);
    }

    private void OnGUI()
    {
        try
        {
            DrawTopBar();

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                // LEFT: Settings
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(position.width * 0.48f)))
                {
                    DrawSettingsPanel();
                    EditorGUILayout.Space(8);
                    DrawActionsPanel();
                }

                // RIGHT: Preview
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawPreviewPanel();
                }
            }
        }
        catch (ExitGUIException)
        {
            // IMPORTANT: never swallow or log this, Unity uses it internally
            throw;
        }
        catch (Exception e)
        {
            EditorGUILayout.HelpBox("Smart Scene Exporter UI error:\n" + e.Message, MessageType.Error);
            Debug.LogException(e);
        }

        if (_autoRefresh)
            RecomputeIfNeeded(false);
    }

    private void DrawTopBar()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Smart Scene Exporter", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            sceneAsset = (SceneAsset)EditorGUILayout.ObjectField(new GUIContent("Scene (.unity)"), sceneAsset, typeof(SceneAsset), false);

            _autoRefresh = GUILayout.Toggle(_autoRefresh, new GUIContent("Auto Refresh"), GUILayout.Width(110));

            if (GUILayout.Button("Force Rebuild", GUILayout.Width(120)))
                RecomputeIfNeeded(true);
        }

        using (new EditorGUI.DisabledScope(true))
        {
            string p = sceneAsset ? AssetDatabase.GetAssetPath(sceneAsset) : "";
            EditorGUILayout.TextField("Scene Path", string.IsNullOrEmpty(p) ? "(none)" : p);
        }
    }

    private void DrawSettingsPanel()
    {
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("HelpBox");

        exportMode = (ExportMode)EditorGUILayout.EnumPopup(new GUIContent("Export Mode"), exportMode);
        EditorGUILayout.HelpBox(GetExportModeDescription(exportMode), MessageType.Info);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Optional Exclusions", EditorStyles.boldLabel);

        excludePluginsFolder = EditorGUILayout.ToggleLeft("Exclude Assets/Plugins", excludePluginsFolder);
        excludeEditorFolders = EditorGUILayout.ToggleLeft("Exclude any 'Editor' folders", excludeEditorFolders);
        excludeAddressablesData = EditorGUILayout.ToggleLeft("Exclude Assets/AddressableAssetsData", excludeAddressablesData);
        excludeStreamingAssets = EditorGUILayout.ToggleLeft("Exclude Assets/StreamingAssets", excludeStreamingAssets);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Extra Include (optional)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Use this for Addressables-only assets, Resources.Load assets, or anything not auto-detected.", MessageType.None);

        int size = Mathf.Max(0, EditorGUILayout.IntField("Size", extraInclude?.Length ?? 0));
        if (extraInclude == null) extraInclude = Array.Empty<UnityEngine.Object>();
        if (size != extraInclude.Length) Array.Resize(ref extraInclude, size);
        for (int i = 0; i < extraInclude.Length; i++)
            extraInclude[i] = EditorGUILayout.ObjectField($"Extra {i}", extraInclude[i], typeof(UnityEngine.Object), false);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Organize Export (Staging)", EditorStyles.boldLabel);
        reorganizeIntoFolders = EditorGUILayout.ToggleLeft("Organize into clean folders (staging)", reorganizeIntoFolders);

        using (new EditorGUI.DisabledScope(!reorganizeIntoFolders))
        {
            stagingRootFolderName = EditorGUILayout.TextField(new GUIContent("Staging Root (under Assets/)"), stagingRootFolderName);
            deleteStagingAfterExport = EditorGUILayout.ToggleLeft("Delete staging after export", deleteStagingAfterExport);

            EditorGUILayout.HelpBox(
                "Staging = TEMP copies inside Assets/. It exports clean folders like Scripts/Materials/Textures.\n" +
                "Delete staging = removes only the temp copies after export (recommended).",
                MessageType.Info);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawActionsPanel()
    {
        EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("HelpBox");

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Select All")) SetAllSelection(true);
            if (GUILayout.Button("Select None")) SetAllSelection(false);
            if (GUILayout.Button("Select Recommended")) SelectRecommended();
        }

        EditorGUILayout.Space(6);

        bool hasScene = sceneAsset != null && AssetDatabase.GetAssetPath(sceneAsset).EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        bool hasSelected = _selected.Any(kv => kv.Value);

        using (new EditorGUI.DisabledScope(!hasScene || !hasSelected))
        {
            string label = reorganizeIntoFolders ? "EXPORT (Organized .unitypackage)" : "EXPORT (.unitypackage)";

            // BLUE primary button
            Color oldColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.24f, 0.49f, 0.90f);

            if (GUILayout.Button(label, GUILayout.Height(38)))
                ExportSelected();

            GUI.backgroundColor = oldColor;
        }

        if (!hasScene)
            EditorGUILayout.HelpBox("Pick a valid SceneAsset (.unity).", MessageType.Warning);
        else if (_computedPaths.Count == 0)
            EditorGUILayout.HelpBox("Preview list is empty.\n" + _lastComputeNote, MessageType.Info);

        EditorGUILayout.EndVertical();
    }

    // -----------------------------
    // Preview (simple like old version)
    // -----------------------------
    private void DrawPreviewPanel()
    {
        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("HelpBox");

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Search", GUILayout.Width(50));
            _search = EditorGUILayout.TextField(_search);

            _showOnlySelected = GUILayout.Toggle(_showOnlySelected, "Selected only", GUILayout.Width(110));
            if (GUILayout.Button("Clear", GUILayout.Width(60))) _search = "";
        }

        EditorGUILayout.Space(4);

        // Small stats line
        int total = _computedPaths.Count;
        int selected = _selected.Count(kv => kv.Value);
        EditorGUILayout.LabelField($"Total: {total}   |   Selected: {selected}", EditorStyles.miniLabel);

        EditorGUILayout.Space(6);

        if (sceneAsset == null)
        {
            EditorGUILayout.HelpBox("Select a scene to see preview.", MessageType.Info);
            EditorGUILayout.EndVertical();
            return;
        }

        if (_computedPaths.Count == 0)
        {
            EditorGUILayout.HelpBox("Nothing to preview.\n" + _lastComputeNote, MessageType.Info);
            EditorGUILayout.EndVertical();
            return;
        }

        // Header
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("", GUILayout.Width(18));      // checkbox
            GUILayout.Label("Name", GUILayout.ExpandWidth(true));
            if (reorganizeIntoFolders)
                GUILayout.Label("Organized", GUILayout.Width(220));
            GUILayout.Label("Ping", GUILayout.Width(44));
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        foreach (var path in FilterForUI(_computedPaths))
        {
            string name = Path.GetFileName(path);

            using (new EditorGUILayout.HorizontalScope())
            {
                bool sel = IsSelected(path);
                bool newSel = EditorGUILayout.Toggle(sel, GUILayout.Width(18));
                if (newSel != sel) _selected[path] = newSel;

                GUILayout.Label(name, GUILayout.ExpandWidth(true));

                if (reorganizeIntoFolders)
                {
                    string staged = $"{GetCategoryFolder(path)}/{name}";
                    GUILayout.Label(staged, EditorStyles.miniLabel, GUILayout.Width(220));
                }

                if (GUILayout.Button("Ping", GUILayout.Width(44)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (obj) EditorGUIUtility.PingObject(obj);
                }
            }
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private IEnumerable<string> FilterForUI(IEnumerable<string> list)
    {
        IEnumerable<string> q = list;

        if (!string.IsNullOrWhiteSpace(_search))
        {
            string s = _search.Trim();
            q = q.Where(p =>
                Path.GetFileName(p).IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                p.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        if (_showOnlySelected)
            q = q.Where(IsSelected);

        return q;
    }

    // -----------------------------
    // Compute
    // -----------------------------
    private void RecomputeIfNeeded(bool force)
    {
        string fp = BuildFingerprint();
        if (!force && fp == _lastFingerprint) return;

        _lastFingerprint = fp;
        RecomputePreview();
        Repaint();
    }

    private string BuildFingerprint()
    {
        string scenePath = sceneAsset ? AssetDatabase.GetAssetPath(sceneAsset) : "";
        string extras = extraInclude == null ? "" : string.Join("|", extraInclude.Where(o => o).Select(o => AssetDatabase.GetAssetPath(o)));

        return string.Join("::",
            scenePath,
            exportMode.ToString(),
            excludePluginsFolder, excludeEditorFolders, excludeAddressablesData, excludeStreamingAssets,
            extras,
            reorganizeIntoFolders, stagingRootFolderName, deleteStagingAfterExport
        );
    }

    private void RecomputePreview()
    {
        _computedPaths.Clear();
        _kind.Clear();
        _lastComputeNote = "";

        if (!sceneAsset)
        {
            _lastComputeNote = "No scene selected.";
            return;
        }

        string scenePath = AssetDatabase.GetAssetPath(sceneAsset);
        if (string.IsNullOrEmpty(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
        {
            _lastComputeNote = "Selected asset is not a valid .unity scene.";
            return;
        }

        var all = new HashSet<string>(AssetDatabase.GetDependencies(scenePath, true));
        all.Add(scenePath);

        // Manual extras (+deps)
        if (extraInclude != null)
        {
            foreach (var obj in extraInclude)
            {
                if (!obj) continue;
                string p = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(p)) continue;

                if (AssetDatabase.IsValidFolder(p))
                {
                    foreach (var a in GetAllAssetsInFolder(p))
                        all.Add(a);
                }
                else
                {
                    all.Add(p);
                    foreach (var d in AssetDatabase.GetDependencies(p, true))
                        all.Add(d);
                }
            }
        }

        var filtered = all
            .Where(IsValidExportableAssetPath)
            .Where(p => !IsExcludedByFolderRules(p))
            .Where(p => IsAllowedByMode(p))
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        _computedPaths = filtered;

        foreach (var p in _computedPaths)
            _kind[p] = Classify(p);

        var nextSel = new Dictionary<string, bool>();
        foreach (var p in _computedPaths)
            nextSel[p] = _selected.TryGetValue(p, out var old) ? old : true;
        _selected = nextSel;

        _lastComputeNote = $"All deps: {all.Count} | After filters: {filtered.Count}";
    }

    // -----------------------------
    // Selection helpers
    // -----------------------------
    private bool IsSelected(string path) => _selected.TryGetValue(path, out var v) && v;

    private void SetAllSelection(bool value)
    {
        var keys = _selected.Keys.ToList();
        foreach (var k in keys) _selected[k] = value;
    }

    private void SelectRecommended()
    {
        foreach (var p in _computedPaths)
        {
            var k = _kind[p];
            bool sel = true;

            // Recommended selection by mode
            if (k == AssetKind.Code && exportMode != ExportMode.CompleteProject) sel = false;
            if (k == AssetKind.Animation && exportMode == ExportMode.VisualsOnly) sel = false;

            _selected[p] = sel;
        }
    }

    // -----------------------------
    // Export
    // -----------------------------
    private void ExportSelected()
    {
        var selectedPaths = _selected.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        if (selectedPaths.Count == 0)
        {
            EditorUtility.DisplayDialog("Smart Scene Exporter", "Nothing selected.", "OK");
            return;
        }

        string scenePath = AssetDatabase.GetAssetPath(sceneAsset);
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        string defaultName = $"{sceneName}_{DateTime.Now:yyyy-MM-dd_HH-mm}.unitypackage";

        string savePath = EditorUtility.SaveFilePanel(
            "Export Package",
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            defaultName,
            "unitypackage"
        );

        if (string.IsNullOrEmpty(savePath)) return;

        try
        {
            if (!reorganizeIntoFolders)
            {
                AssetDatabase.ExportPackage(selectedPaths.ToArray(), savePath, ExportPackageOptions.Interactive);
                Debug.Log($"[SmartSceneExporter] Exported {selectedPaths.Count} assets to:\n{savePath}");
                return;
            }

            // Organized export: stage -> export -> cleanup
            string safeRoot = MakeSafeFolderName(stagingRootFolderName);
            if (string.IsNullOrWhiteSpace(safeRoot)) safeRoot = "__SceneExport_Staging";

            string stamp = $"{sceneName}_{DateTime.Now:yyyyMMdd_HHmmss}";
            EnsureFolder("Assets", safeRoot);
            EnsureFolder($"Assets/{safeRoot}", stamp);

            string stageBase = $"Assets/{safeRoot}/{stamp}";

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var src in selectedPaths)
                {
                    string dst = GetStagedPath(stageBase, src);
                    EnsureFolderPathToFile(dst);
                    dst = MakeUniqueAssetPath(dst);
                    AssetDatabase.CopyAsset(src, dst);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            var stageAssets = GetAllAssetsInFolder(stageBase)
                .Where(IsValidExportableAssetPath)
                .OrderBy(p => p)
                .ToArray();

            if (stageAssets.Length == 0)
                throw new Exception("Staging folder ended up empty. Export aborted.");

            AssetDatabase.ExportPackage(stageAssets, savePath, ExportPackageOptions.Interactive);
            Debug.Log($"[SmartSceneExporter] Exported ORGANIZED package ({stageAssets.Length} staged assets) to:\n{savePath}");

            if (deleteStagingAfterExport)
            {
                FileUtil.DeleteFileOrDirectory(stageBase);
                FileUtil.DeleteFileOrDirectory(stageBase + ".meta");
                AssetDatabase.Refresh();
            }
            else
            {
                Debug.Log($"[SmartSceneExporter] Staging kept at: {stageBase}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[SmartSceneExporter] Export failed: {e}");
            EditorUtility.DisplayDialog("Export Failed", e.Message, "OK");
        }
    }

    // -----------------------------
    // Mode / exclusions
    // -----------------------------
    private bool IsAllowedByMode(string assetPath)
    {
        if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            return true;

        string ext = Path.GetExtension(assetPath).ToLowerInvariant();
        bool isCode = ext == ".cs" || ext == ".asmdef" || ext == ".asmref" || ext == ".dll" || ext == ".rsp";
        bool isAnim = ext == ".anim" || ext == ".controller" || ext == ".overridecontroller" || ext == ".mask" || ext == ".playable" || ext == ".timeline";

        return exportMode switch
        {
            ExportMode.VisualsOnly => !isCode && !isAnim,
            ExportMode.VisualsAndAnimations => !isCode,
            _ => true
        };
    }

    private bool IsValidExportableAssetPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;
        if (AssetDatabase.IsValidFolder(p)) return false;
        return true;
    }

    private bool IsExcludedByFolderRules(string p)
    {
        if (excludePluginsFolder && p.StartsWith("Assets/Plugins/", StringComparison.OrdinalIgnoreCase)) return true;
        if (excludeAddressablesData && p.StartsWith("Assets/AddressableAssetsData/", StringComparison.OrdinalIgnoreCase)) return true;
        if (excludeStreamingAssets && p.StartsWith("Assets/StreamingAssets/", StringComparison.OrdinalIgnoreCase)) return true;

        if (excludeEditorFolders)
        {
            var segments = p.Split('/');
            for (int i = 0; i < segments.Length; i++)
                if (string.Equals(segments[i], "Editor", StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        return false;
    }

    private AssetKind Classify(string path)
    {
        if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return AssetKind.Scene;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".cs" || ext == ".asmdef" || ext == ".asmref" || ext == ".dll" || ext == ".rsp") return AssetKind.Code;
        if (ext == ".anim" || ext == ".controller" || ext == ".overridecontroller" || ext == ".mask" || ext == ".playable" || ext == ".timeline") return AssetKind.Animation;

        return AssetKind.Other;
    }

    // -----------------------------
    // Staging layout
    // -----------------------------
    private string GetStagedPath(string stageBase, string srcPath)
    {
        string file = Path.GetFileName(srcPath);
        string cat = GetCategoryFolder(srcPath);
        return $"{stageBase}/{cat}/{file}";
    }

    private string GetCategoryFolder(string assetPath)
    {
        if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return "Scenes";

        string ext = Path.GetExtension(assetPath).ToLowerInvariant();
        var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);

        if (ext == ".cs" || ext == ".asmdef" || ext == ".asmref" || ext == ".dll" || ext == ".rsp") return "Scripts";
        if (ext == ".anim" || ext == ".controller" || ext == ".overridecontroller" || ext == ".mask" || ext == ".playable" || ext == ".timeline") return "Animations";
        if (ext == ".prefab") return "Prefabs";
        if (ext == ".mat") return "Materials";
        if (ext == ".shader" || ext == ".shadergraph" || ext == ".shadersubgraph" || ext == ".compute") return "Shaders";
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga" || ext == ".psd" || ext == ".tif" || ext == ".tiff" || ext == ".exr" || ext == ".hdr") return "Textures";
        if (ext == ".fbx" || ext == ".obj" || ext == ".dae" || ext == ".blend") return "Models";
        if (ext == ".wav" || ext == ".mp3" || ext == ".ogg" || ext == ".aiff" || ext == ".aif") return "Audio";
        if (ext == ".ttf" || ext == ".otf") return "Fonts";
        if (ext == ".vfx" || ext == ".vfxgraph") return "VFX";
        if (ext == ".asset") return "Data";

        if (type == typeof(Material)) return "Materials";
        if (type == typeof(Shader)) return "Shaders";
        if (type == typeof(AnimationClip)) return "Animations";
        if (type == typeof(AudioClip)) return "Audio";
        if (type == typeof(Font)) return "Fonts";
        if (typeof(Texture).IsAssignableFrom(type)) return "Textures";

        return "Other";
    }

    // -----------------------------
    // Helpers
    // -----------------------------
    private string GetExportModeDescription(ExportMode mode)
    {
        switch (mode)
        {
            case ExportMode.VisualsOnly:
                return "Exports the scene with visual assets only (models, materials, textures, prefabs, shaders). No animations or scripts included.";
            case ExportMode.VisualsAndAnimations:
                return "Exports visual assets plus animation clips and controllers. Scripts are not included.";
            case ExportMode.CompleteProject:
                return "Full export including visuals, animations, and all related scripts/assemblies. Recommended for full scene transfer.";
            default:
                return "";
        }
    }

    private static void EnsureFolder(string parent, string child)
    {
        string path = $"{parent}/{child}";
        if (!AssetDatabase.IsValidFolder(path))
            AssetDatabase.CreateFolder(parent, child);
    }

    private static void EnsureFolderPathToFile(string assetFilePath)
    {
        string dir = Path.GetDirectoryName(assetFilePath)?.Replace("\\", "/");
        if (string.IsNullOrEmpty(dir)) return;
        if (!dir.StartsWith("Assets")) return;

        var parts = dir.Split('/');
        string current = "Assets";
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private static string MakeUniqueAssetPath(string desiredPath)
    {
        if (!File.Exists(desiredPath)) return desiredPath;

        string dir = Path.GetDirectoryName(desiredPath)?.Replace("\\", "/") ?? "Assets";
        string file = Path.GetFileNameWithoutExtension(desiredPath);
        string ext = Path.GetExtension(desiredPath);

        for (int i = 1; i < 9999; i++)
        {
            string candidate = $"{dir}/{file}_{i}{ext}";
            if (!File.Exists(candidate))
                return candidate;
        }
        return desiredPath;
    }

    private static string MakeSafeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        cleaned = cleaned.Replace(" ", "_");
        return cleaned;
    }

    private IEnumerable<string> GetAllAssetsInFolder(string folderPath)
    {
        string[] guids = AssetDatabase.FindAssets("", new[] { folderPath });
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
                yield return path;
        }
    }
}
#endif
