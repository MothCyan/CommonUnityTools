#if UNITY_EDITOR
using System;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class TMPFontApplierWindow : EditorWindow
{
    private TMP_FontAsset _targetFont;

    private bool _applyToPrefabs = true;
    private bool _applyToScenes = true;
    private bool _setTmpSettingsDefault = false;

    private Vector2 _scroll;
    private string _log;

    [MenuItem("Tools/Fonts/TMP 一键替换字体")]
    private static void Open()
    {
        var win = GetWindow<TMPFontApplierWindow>(utility: false, title: "TMP Font Applier");
        win.minSize = new Vector2(520, 420);
        win.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("选择一个 TMP_FontAsset，然后批量替换项目中所有 TMP_Text 的 font 引用。", EditorStyles.wordWrappedLabel);
        EditorGUILayout.Space(8);

        _targetFont = (TMP_FontAsset)EditorGUILayout.ObjectField("目标字体(TMP_FontAsset)", _targetFont, typeof(TMP_FontAsset), allowSceneObjects: false);

        EditorGUILayout.Space(8);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("应用范围", EditorStyles.boldLabel);
            _applyToPrefabs = EditorGUILayout.ToggleLeft("Prefab (Assets 内所有 Prefab)", _applyToPrefabs);
            _applyToScenes = EditorGUILayout.ToggleLeft("Scene (Assets 内所有 Scene)", _applyToScenes);
            _setTmpSettingsDefault = EditorGUILayout.ToggleLeft("同时设置 TMP_Settings 默认字体(新建TMP默认)", _setTmpSettingsDefault);
        }

        EditorGUILayout.Space(8);
        using (new EditorGUI.DisabledScope(_targetFont == null || (!_applyToPrefabs && !_applyToScenes && !_setTmpSettingsDefault)))
        {
            if (GUILayout.Button("开始替换", GUILayout.Height(34)))
            {
                Apply();
            }
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("日志", EditorStyles.boldLabel);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.TextArea(_log ?? string.Empty, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    private void Apply()
    {
        if (_targetFont == null)
        {
            AppendLog("未选择目标字体。\n");
            return;
        }

        int changedTextCount = 0;
        int changedAssetCount = 0;

        try
        {
            AssetDatabase.StartAssetEditing();

            if (_setTmpSettingsDefault)
            {
                if (TrySetTmpDefaultFont(_targetFont, out string msg))
                    AppendLog(msg + "\n");
                else
                    AppendLog("WARNING: 设置 TMP_Settings 默认字体失败：" + msg + "\n");
            }

            if (_applyToPrefabs)
                ApplyToPrefabs(_targetFont, ref changedTextCount, ref changedAssetCount);

            if (_applyToScenes)
                ApplyToScenes(_targetFont, ref changedTextCount, ref changedAssetCount);
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        AppendLog($"完成。修改 TMP_Text 数量={changedTextCount}；修改资源数量(含Prefab/Scene)={changedAssetCount}\n");
    }

    private static bool TrySetTmpDefaultFont(TMP_FontAsset font, out string message)
    {
        // 兼容性说明：不同 TMP 版本对 defaultFontAsset 的暴露方式不同。
        // - 有些版本是 static 只读属性（无法直接赋值）
        // - 有些版本是实例字段序列化为 m_defaultFontAsset
        // 这里统一用 SerializedObject 修改 TMP_Settings 资源。

        TMP_Settings settings = TMP_Settings.instance;
        if (settings == null)
        {
            message = "TMP_Settings.instance 为 null";
            return false;
        }

        try
        {
            var so = new SerializedObject(settings);
            var prop = so.FindProperty("m_defaultFontAsset");
            if (prop == null)
            {
                message = "找不到序列化字段 m_defaultFontAsset（TMP 版本不匹配）";
                return false;
            }

            prop.objectReferenceValue = font;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            message = $"已设置 TMP_Settings.m_defaultFontAsset = {font.name}";
            return true;
        }
        catch (Exception e)
        {
            message = e.Message;
            return false;
        }
    }

    private static void ApplyToPrefabs(TMP_FontAsset font, ref int changedTextCount, ref int changedAssetCount)
    {
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) continue;

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null) continue;

            bool changedThisPrefab = false;
            try
            {
                foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (tmp == null) continue;
                    if (tmp.font == font) continue;

                    tmp.font = font;
                    EditorUtility.SetDirty(tmp);
                    changedTextCount++;
                    changedThisPrefab = true;
                }

                if (changedThisPrefab)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    changedAssetCount++;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }

    private static void ApplyToScenes(TMP_FontAsset font, ref int changedTextCount, ref int changedAssetCount)
    {
        string[] sceneGuids = AssetDatabase.FindAssets("t:Scene");
        foreach (string guid in sceneGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) continue;
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;

            Scene scene;
            try { scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single); }
            catch { continue; }

            bool changedThisScene = false;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (tmp == null) continue;
                    if (tmp.font == font) continue;

                    tmp.font = font;
                    EditorUtility.SetDirty(tmp);
                    changedTextCount++;
                    changedThisScene = true;
                }
            }

            if (changedThisScene)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                changedAssetCount++;
            }
        }
    }

    private void AppendLog(string msg)
    {
        _log = (_log ?? string.Empty) + msg;
        Repaint();
    }
}
#endif
