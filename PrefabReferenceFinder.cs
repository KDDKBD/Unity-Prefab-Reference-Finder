using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using System.Text;

public class PrefabReferenceFinder : EditorWindow
{
    private const string DefaultPrefabsFolderPath = "Assets";
    private const string PrefsKey = "PrefabReferenceFinder_SearchPath";
    private string PrefabsFolderPath
    {
        get => EditorPrefs.GetString(PrefsKey, DefaultPrefabsFolderPath);
        set => EditorPrefs.SetString(PrefsKey, value);
    }
    
    private const string CacheFileName = "PrefabReferenceCache.json";
    private static Dictionary<string, List<string>> referenceCache = new Dictionary<string, List<string>>();
    private static Dictionary<string, HashSet<string>> dependencyMap = new Dictionary<string, HashSet<string>>();
    
    private GameObject targetPrefab;
    private Vector2 scrollPositionReferences;
    private Vector2 scrollPositionDependencies;
    private List<string> referenceResults = new List<string>();
    private List<string> dependencyResults = new List<string>();
    private static bool isSearching;
    private static bool cacheInitialized;
    private List<string> allPrefabPaths = new List<string>();
    private int cacheProgressIndex;
    private int cacheItemsPerFrame = 20;
    private static bool cancelCacheBuilding = false;
    
    // 分类存储依赖项
    private List<string> prefabDependencies = new List<string>();
    private List<string> textureDependencies = new List<string>();
    private List<string> scriptDependencies = new List<string>();
    private List<string> otherDependencies = new List<string>();
    
    [MenuItem("Assets/Find Reference in Prefabs", false, 20)]
    private static void FindReferences()
    {
        var selected = Selection.activeObject as GameObject;
        if (selected == null || PrefabUtility.GetPrefabAssetType(selected) == PrefabAssetType.NotAPrefab)
        {
            EditorUtility.DisplayDialog("Error", "Please select a valid prefab", "OK");
            return;
        }

        var window = GetWindow<PrefabReferenceFinder>("Prefab References");
        window.targetPrefab = selected;
        window.titleContent = new GUIContent($"Prefab: {selected.name}");
        
        if (LoadCacheFromDisk() || cacheInitialized)
        {
            cacheInitialized = true;
            window.FindReferencesWithCache();
        }
        else
        {
            window.BuildCache();
            window.FindReferencesWithCache();
            cacheInitialized = true;
        }
    }

    private void BuildCache()
    {
        cancelCacheBuilding = false;
        referenceCache.Clear();
        dependencyMap.Clear();
        isSearching = true;
        referenceResults.Clear();
        dependencyResults.Clear();
        allPrefabPaths = GetPrefabPathsInFolder(PrefabsFolderPath);
        cacheProgressIndex = 0;
        EditorApplication.update += ProcessCacheFrame;
    }

    private List<string> GetPrefabPathsInFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) 
        {
            Debug.LogError($"Folder not found: {folderPath}");
            return new List<string>();
        }

        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath });
        return prefabGuids.Select(AssetDatabase.GUIDToAssetPath).ToList();
    }

    private void ProcessCacheFrame()
    {
        if (cacheProgressIndex >= allPrefabPaths.Count || cancelCacheBuilding)
        {
            EditorApplication.update -= ProcessCacheFrame;
            isSearching = false;
        
            if (cancelCacheBuilding)
            {
                Debug.Log("Cache building cancelled");
                referenceCache.Clear();
                dependencyMap.Clear();
                cacheInitialized = false;
            }
            else
            {
                cacheInitialized = true;
                SaveCacheToDisk();
                FindReferencesWithCache();
            }
        
            Repaint();
            EditorUtility.ClearProgressBar();
            cancelCacheBuilding = false;
            return;
        }

        int endIndex = Mathf.Min(cacheProgressIndex + cacheItemsPerFrame, allPrefabPaths.Count);

        try
        {
            for (int i = cacheProgressIndex; i < endIndex; i++)
            {
                string path = allPrefabPaths[i];
        
                if (EditorUtility.DisplayCancelableProgressBar("Building Cache", 
                        $"Processing: {Path.GetFileName(path)} ({i+1}/{allPrefabPaths.Count})", 
                        (float)i / allPrefabPaths.Count))
                {
                    cancelCacheBuilding = true;
                    break;
                }
        
                ProcessPrefabDependencies(path);
            }
        }
        finally
        {
            if (cancelCacheBuilding || cacheProgressIndex >= allPrefabPaths.Count)
            {
                EditorUtility.ClearProgressBar();
            }
        }
    
        cacheProgressIndex = endIndex;
        Repaint();
    }

    private void ProcessPrefabDependencies(string prefabPath)
    {
        try
        {
            string[] dependencies = AssetDatabase.GetDependencies(prefabPath, false);
            dependencyMap[prefabPath] = new HashSet<string>(dependencies);
            
            foreach (string dependency in dependencies)
            {
                if (!referenceCache.ContainsKey(dependency))
                {
                    referenceCache[dependency] = new List<string>();
                }
                
                if (!referenceCache[dependency].Contains(prefabPath))
                {
                    referenceCache[dependency].Add(prefabPath);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to process prefab: {prefabPath}\n{e}");
        }
    }

    private void FindReferencesWithCache()
    {
        if (targetPrefab == null) return;
        
        string targetPath = AssetDatabase.GetAssetPath(targetPrefab);
        referenceResults.Clear();
        dependencyResults.Clear();

        // 获取引用结果
        if (referenceCache.TryGetValue(targetPath, out var references))
        {
            referenceResults = references;
        }
        
        // 获取依赖结果
        if (dependencyMap.TryGetValue(targetPath, out var dependencies))
        {
            dependencyResults = dependencies.ToList();
        }
        
        // 对引用列表按字母顺序排序
        referenceResults.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        
        // 对依赖项进行分类和排序
        ClassifyAndSortDependencies();
        
        Repaint();
        EditorUtility.ClearProgressBar();
    }

    // 分类并排序依赖项
    private void ClassifyAndSortDependencies()
    {
        prefabDependencies.Clear();
        textureDependencies.Clear();
        scriptDependencies.Clear();
        otherDependencies.Clear();
        
        foreach (string path in dependencyResults)
        {
            string extension = Path.GetExtension(path).ToLower();
            
            if (extension == ".prefab")
            {
                prefabDependencies.Add(path);
            }
            else if (IsTextureExtension(extension))
            {
                textureDependencies.Add(path);
            }
            else if (IsScriptExtension(extension))
            {
                scriptDependencies.Add(path);
            }
            else
            {
                otherDependencies.Add(path);
            }
        }
        
        // 每个类别内按字母顺序排序
        prefabDependencies.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        textureDependencies.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        scriptDependencies.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        otherDependencies.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsTextureExtension(string extension)
    {
        string[] textureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".gif", ".bmp", ".psd", ".exr", ".hdr" };
        return textureExtensions.Contains(extension);
    }

    private bool IsScriptExtension(string extension)
    {
        string[] scriptExtensions = { ".cs", ".js", ".shader", ".asmdef", ".cginc", ".hlsl", ".glslinc", ".template" };
        return scriptExtensions.Contains(extension);
    }

    private static bool LoadCacheFromDisk()
    {
        string cachePath = Path.Combine(Application.dataPath, "..", CacheFileName);
        if (!File.Exists(cachePath)) 
        {
            Debug.Log("Cache file not found, will build new cache");
            return false;
        }

        try
        {
            string json = File.ReadAllText(cachePath);
            var data = JsonUtility.FromJson<CacheData>(json);
            
            if (data == null || data.entries == null)
            {
                Debug.LogError("Cache data is corrupted");
                return false;
            }
            
            referenceCache = new Dictionary<string, List<string>>();
            foreach (var entry in data.entries)
            {
                if (string.IsNullOrEmpty(entry.key)) continue;
                referenceCache[entry.key] = entry.values ?? new List<string>();
            }
            
            dependencyMap.Clear();
            foreach (var kvp in referenceCache)
            {
                if (kvp.Value == null) continue;
                foreach (var referencingPath in kvp.Value)
                {
                    if (string.IsNullOrEmpty(referencingPath)) continue;
                    if (!dependencyMap.ContainsKey(referencingPath))
                    {
                        dependencyMap[referencingPath] = new HashSet<string>();
                    }
                    dependencyMap[referencingPath].Add(kvp.Key);
                }
            }
            
            Debug.Log($"Successfully loaded cache with {referenceCache.Count} entries");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load cache: {e}");
            return false;
        }
    }

    private static void SaveCacheToDisk()
    {
        try
        {
            var data = new CacheData();
            data.entries = new List<CacheEntry>();
            
            foreach (var kvp in referenceCache)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                var entry = new CacheEntry
                {
                    key = kvp.Key,
                    values = kvp.Value ?? new List<string>()
                };
                data.entries.Add(entry);
            }
            
            string json = JsonUtility.ToJson(data, true);
            string cachePath = Path.Combine(Application.dataPath, "..", CacheFileName);
            File.WriteAllText(cachePath, json, Encoding.UTF8);
            Debug.Log($"Cache saved successfully with {data.entries.Count} entries");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save cache: {e}");
        }
    }

    [System.Serializable]
    private class CacheData
    {
        public List<CacheEntry> entries = new List<CacheEntry>();
    }

    [System.Serializable]
    private class CacheEntry
    {
        public string key;
        public List<string> values;
    }

    private void OnEnable()
    {
        EditorApplication.projectChanged += OnProjectChanged;
        wantsMouseMove = true;
    }

    private void OnDisable()
    {
        EditorApplication.projectChanged -= OnProjectChanged;
        EditorApplication.update -= ProcessCacheFrame;
        EditorUtility.ClearProgressBar();
    }

    private static void OnProjectChanged()
    {
        cacheInitialized = false;
    }

    private void OnGUI()
    {
        HandleDragAndDrop();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Optimized Prefab Reference Finder", EditorStyles.boldLabel);
        EditorGUILayout.Separator();

        if (targetPrefab == null)
        {
            EditorGUILayout.HelpBox("No target prefab selected. Drag a prefab here to search.", MessageType.Info);
            return;
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"Target Prefab: {targetPrefab.name}", EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Change Target", GUILayout.Width(120)))
        {
            Selection.activeObject = targetPrefab;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Separator();
        
        // 添加搜索路径设置
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Search Path:", GUILayout.Width(80));
        EditorGUILayout.LabelField(PrefabsFolderPath, EditorStyles.textField, GUILayout.ExpandWidth(true));
        
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            string newPath = EditorUtility.OpenFolderPanel("Select Folder to Search", PrefabsFolderPath, "");
            if (!string.IsNullOrEmpty(newPath))
            {
                // 将绝对路径转换为相对于项目文件夹的路径
                string projectPath = Application.dataPath.Replace("Assets", "");
                if (newPath.StartsWith(projectPath))
                {
                    PrefabsFolderPath = newPath.Substring(projectPath.Length);
                    cacheInitialized = false;
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "The selected folder must be within the project.", "OK");
                }
            }
        }
        
        if (GUILayout.Button("Reset", GUILayout.Width(60)))
        {
            PrefabsFolderPath = DefaultPrefabsFolderPath;
            cacheInitialized = false;
        }
        EditorGUILayout.EndHorizontal();

        // 缓存控制区域
        if (GUILayout.Button("Rebuild Cache", GUILayout.Width(120)))
        {
            cacheInitialized = false;
            BuildCache();
        }
        
        string cacheStatus = cacheInitialized ? 
            "Cache initialized. Subsequent searches will be instant." : 
            "Building cache... First search may take longer.";
            
        if (isSearching)
        {
            EditorGUILayout.BeginHorizontal();
            float progress = (float)cacheProgressIndex / allPrefabPaths.Count;
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(), progress, 
                $"Processing: {cacheProgressIndex}/{allPrefabPaths.Count} prefabs");
        
            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                cancelCacheBuilding = true;
            }
            EditorGUILayout.EndHorizontal();
        }
        else
        {
            EditorGUILayout.HelpBox(cacheStatus, MessageType.Info);
        }
        
        EditorGUILayout.HelpBox("Drag any prefab into this window to search for its references and dependencies.", MessageType.Info);

        if (!isSearching)
        {
            EditorGUILayout.Separator();
            
            EditorGUILayout.BeginHorizontal();
            
            // 左侧：引用列表
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f));
            try
            {
                EditorGUILayout.LabelField($"References to {targetPrefab.name}: {referenceResults.Count}", EditorStyles.boldLabel);
                scrollPositionReferences = EditorGUILayout.BeginScrollView(scrollPositionReferences, "box");
            
                if (referenceResults.Count == 0)
                {
                    EditorGUILayout.HelpBox("No references found", MessageType.Info);
                }
                else
                {
                    foreach (string path in referenceResults)
                    {
                        DisplayAssetItem(path);
                    }
                }
            }
            finally
            {
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            
            // 右侧：依赖列表
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f));
            try
            {
                EditorGUILayout.LabelField($"Dependencies of {targetPrefab.name}: {dependencyResults.Count}", EditorStyles.boldLabel);
                scrollPositionDependencies = EditorGUILayout.BeginScrollView(scrollPositionDependencies, "box");
            
                if (dependencyResults.Count == 0)
                {
                    EditorGUILayout.HelpBox("No dependencies found", MessageType.Info);
                }
                else
                {
                    // 按分类显示依赖项
                    DisplayDependencyCategory("Prefabs", prefabDependencies);
                    DisplayDependencyCategory("Textures", textureDependencies);
                    DisplayDependencyCategory("Scripts", scriptDependencies);
                    DisplayDependencyCategory("Other", otherDependencies);
                }
            }
            finally
            {
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }

            
            EditorGUILayout.EndHorizontal();
        }
    }

    private void DisplayDependencyCategory(string categoryName, List<string> paths)
    {
        if (paths.Count == 0) return;
        
        EditorGUILayout.LabelField($"{categoryName} ({paths.Count})", EditorStyles.boldLabel);
        foreach (string path in paths)
        {
            DisplayAssetItem(path);
        }
    }

    private void DisplayAssetItem(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        
        EditorGUILayout.BeginHorizontal(GUILayout.Height(24));
        
        UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
        Texture2D icon = EditorGUIUtility.FindTexture("DefaultAsset Icon") as Texture2D;

        if (asset != null)
        {
            var content = EditorGUIUtility.ObjectContent(asset, asset.GetType());
            if (content != null && content.image != null)
            {
                icon = content.image as Texture2D;
            }
        }
        
        string fileName = Path.GetFileName(path);
        GUIContent labelContent = new GUIContent(fileName, icon, path);
        
        EditorGUILayout.LabelField(labelContent, 
            EditorStyles.label, 
            GUILayout.MinWidth(50), 
            GUILayout.ExpandWidth(true));
        
        if (GUILayout.Button("Select", GUILayout.Width(60)))
        {
            UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (obj != null)
            {
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }
        }
        
        if (GUILayout.Button("Open", GUILayout.Width(60)))
        {
            UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (obj != null)
            {
                AssetDatabase.OpenAsset(obj);
            }
        }
        
        EditorGUILayout.EndHorizontal();
    }

    private void HandleDragAndDrop()
    {
        Rect dropArea = new Rect(0, 0, position.width, position.height);
        GUI.Box(dropArea, "", GUIStyle.none);

        Event evt = Event.current;
        EventType eventType = evt.type;

        if (!dropArea.Contains(evt.mousePosition))
            return;

        switch (eventType)
        {
            case EventType.DragUpdated:
            case EventType.DragPerform:
                bool isPrefab = false;
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is GameObject gameObj && 
                        PrefabUtility.GetPrefabAssetType(gameObj) != PrefabAssetType.NotAPrefab)
                    {
                        isPrefab = true;
                        break;
                    }
                }

                if (isPrefab)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    
                    if (eventType == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        
                        foreach (var obj in DragAndDrop.objectReferences)
                        {
                            if (obj is GameObject gameObj && 
                                PrefabUtility.GetPrefabAssetType(gameObj) != PrefabAssetType.NotAPrefab)
                            {
                                targetPrefab = gameObj;
                                titleContent = new GUIContent($"Prefab: {targetPrefab.name}");
                                
                                if (LoadCacheFromDisk() || cacheInitialized)
                                {
                                    cacheInitialized = true;
                                    FindReferencesWithCache();
                                }
                                else
                                {
                                    BuildCache();
                                }
                                break;
                            }
                        }
                        
                        evt.Use();
                    }
                }
                else
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                }
                break;
        }
    }
}
