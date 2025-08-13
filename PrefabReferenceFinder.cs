using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using System.Text;

public class PrefabReferenceFinder : EditorWindow
{
    private const string PrefabsFolderPath = "Assets"; // "Assets/Resources/UI"
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
    private bool useCache = true;
    private List<string> allPrefabPaths = new List<string>();
    private int cacheProgressIndex;
    private int cacheItemsPerFrame = 20;
    private static bool cancelCacheBuilding = false;    // 添加取消标志
    
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
        
        if (!cacheInitialized)
        {
            window.InitializeCache();
        }
        else
        {
            window.FindReferencesWithCache();
        }
    }

    private void InitializeCache()
    {
        if (cacheInitialized) return;
        
        if (LoadCacheFromDisk())
        {
            cacheInitialized = true;
            FindReferencesWithCache();
            return;
        }

        BuildCache();
    }

    private void BuildCache()
    {
        cancelCacheBuilding = false; // 重置取消标志
        
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
        // 增加取消检查
        if (cacheProgressIndex >= allPrefabPaths.Count || cancelCacheBuilding)
        {
            EditorApplication.update -= ProcessCacheFrame;
            isSearching = false;
        
            if (cancelCacheBuilding)
            {
                // 取消时的处理
                Debug.Log("Cache building cancelled");
                referenceCache.Clear();
                dependencyMap.Clear();
                cacheInitialized = false;
            }
            else
            {
                // 正常完成
                cacheInitialized = true;
                SaveCacheToDisk();
                FindReferencesWithCache();
            }
        
            Repaint();
            EditorUtility.ClearProgressBar();
            cancelCacheBuilding = false; // 重置标志
            return;
        }

        int endIndex = Mathf.Min(cacheProgressIndex + cacheItemsPerFrame, allPrefabPaths.Count);

        try
        {
            for (int i = cacheProgressIndex; i < endIndex; i++)
            {
                string path = allPrefabPaths[i];
        
                // 使用可取消的进度条
                if (EditorUtility.DisplayCancelableProgressBar("Building Cache", 
                        $"Processing: {Path.GetFileName(path)} ({i+1}/{allPrefabPaths.Count})", 
                        (float)i / allPrefabPaths.Count))
                {
                    // 用户点击了取消按钮
                    cancelCacheBuilding = true;
                    break;
                }
        
                ProcessPrefabDependencies(path);
            }
        }
        finally
        {
            // 确保在循环结束后清理进度条
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

        if (useCache)
        {
            // 获取引用结果（哪些prefab引用了当前目标）
            if (referenceCache.TryGetValue(targetPath, out var references))
            {
                referenceResults = references;
            }
            
            // 获取依赖结果（当前目标依赖了哪些资源）
            if (dependencyMap.TryGetValue(targetPath, out var dependencies))
            {
                dependencyResults = dependencies.ToList();
            }
        }
        else
        {
            FindReferencesDirect();
        }
        
        Repaint();
        EditorUtility.ClearProgressBar();
    }

    private void FindReferencesDirect()
    {
        string targetPath = AssetDatabase.GetAssetPath(targetPrefab);
        referenceResults.Clear();
        dependencyResults.Clear();

        // 直接查找引用
        referenceResults = allPrefabPaths
            .Where(path => dependencyMap.TryGetValue(path, out var deps) && deps.Contains(targetPath))
            .ToList();
        
        // 直接查找依赖
        dependencyResults = AssetDatabase.GetDependencies(targetPath, false).ToList();
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
            
            if (data == null)
            {
                Debug.LogError("Failed to load cache: Deserialized data is null");
                return false;
            }
            
            if (data.entries == null)
            {
                Debug.LogError("Cache data is corrupted: entries list is null");
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
            
            try
            {
                string backupPath = cachePath + ".corrupted";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(cachePath, backupPath);
                Debug.Log($"Moved corrupted cache to {backupPath}");
            }
            catch (Exception backupEx)
            {
                Debug.LogError($"Failed to backup corrupted cache: {backupEx}");
            }
            
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
        // 允许接受拖拽
        wantsMouseMove = true;
    }

    private void OnDisable()
    {
        EditorApplication.projectChanged -= OnProjectChanged;
        EditorApplication.update -= ProcessCacheFrame;
        EditorUtility.ClearProgressBar();
        
        // // 重置GUI状态
        // GUIUtility.ExitGUI();
    }

    private static void OnProjectChanged()
    {
        cacheInitialized = false;
    }

    private void OnGUI()
    {
        // 处理拖拽事件
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
        EditorGUILayout.LabelField($"Search Path: {PrefabsFolderPath}", EditorStyles.miniLabel);

        // 缓存控制区域
        EditorGUILayout.BeginHorizontal();
        useCache = EditorGUILayout.Toggle("Use Cache", useCache, GUILayout.Width(150));
        
        if (GUILayout.Button(cacheInitialized ? "Refresh Cache" : "Build Cache", GUILayout.Width(120)))
        {
            cacheInitialized = false;
            InitializeCache();
        }
        
        EditorGUILayout.EndHorizontal();
        
        string cacheStatus = cacheInitialized ? 
            "Cache initialized. Subsequent searches will be instant." : 
            "Building cache... First search may take longer.";
            
        if (isSearching)
        {
            // 添加取消按钮
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
        
        // 拖拽提示
        EditorGUILayout.HelpBox("Drag any prefab into this window to search for its references and dependencies.", MessageType.Info);

        // 结果区域
        if (!isSearching)
        {
            EditorGUILayout.Separator();
            
            // 使用水平布局并排显示引用和依赖
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
                    foreach (string path in dependencyResults)
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

            
            EditorGUILayout.EndHorizontal();
        }
    }

    private void DisplayAssetItem(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        
        EditorGUILayout.BeginHorizontal(GUILayout.Height(24));
        
        UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
        System.Type assetType = asset?.GetType();
        
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
        // 创建覆盖整个窗口的拖拽区域
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
                // 检查拖拽对象是否为预制体
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
                    // 显示拖拽视觉反馈
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    
                    if (eventType == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        
                        // 获取第一个有效的预制体
                        foreach (var obj in DragAndDrop.objectReferences)
                        {
                            if (obj is GameObject gameObj && 
                                PrefabUtility.GetPrefabAssetType(gameObj) != PrefabAssetType.NotAPrefab)
                            {
                                // 设置新的目标预制体
                                targetPrefab = gameObj;
                                titleContent = new GUIContent($"Prefab: {targetPrefab.name}");
                                
                                // 触发搜索
                                if (cacheInitialized)
                                {
                                    FindReferencesWithCache();
                                }
                                else
                                {
                                    InitializeCache();
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
