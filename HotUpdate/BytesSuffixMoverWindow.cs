#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 工具：将选定资源(文件)复制到目标目录，并在目标端追加 .bytes 后缀（覆盖同名文件）。
/// 
/// 典型用途：把某些需要以 TextAsset/.bytes 形式放入特定目录的文件（如 json/bin 等）
/// 一键复制并重命名为 *.bytes。
/// 
/// 说明：
/// - 源可以选择任意文件（支持多选）。
/// - 目标是磁盘目录（建议在项目内，如 Assets/StreamingAssets 或 Assets/Resources 等）。
/// - 复制行为：始终覆盖同名文件。
/// - 默认会保留源文件名并追加 .bytes，例如 a.json -> a.json.bytes
/// </summary>
public sealed class BytesSuffixMoverWindow : EditorWindow
{
    private UnityEngine.Object[] _sources;

    // 允许从系统选择任意文件（不要求在 Project 内）
    private readonly System.Collections.Generic.List<string> _sourceFilePaths = new System.Collections.Generic.List<string>();

    private string _targetFolder = "";

    private bool _keepSubfolders = false;
    private bool _stripOriginalExtension = false;

    private const string PrefKeyPrefix = "WxGame.BytesSuffixMover.";

    private void OnEnable()
    {
        LoadPrefs();
    }

    private void OnDisable()
    {
        SavePrefs();
    }

    private void LoadPrefs()
    {
        _targetFolder = EditorPrefs.GetString(PrefKeyPrefix + "TargetFolder", _targetFolder);
        _keepSubfolders = EditorPrefs.GetBool(PrefKeyPrefix + "KeepSubfolders", _keepSubfolders);
        _stripOriginalExtension = EditorPrefs.GetBool(PrefKeyPrefix + "StripExt", _stripOriginalExtension);

        _sourceFilePaths.Clear();
        var joined = EditorPrefs.GetString(PrefKeyPrefix + "SourceFiles", string.Empty);
        if (!string.IsNullOrEmpty(joined))
        {
            var parts = joined.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var full = p.Trim();
                if (!string.IsNullOrEmpty(full))
                    _sourceFilePaths.Add(full);
            }
        }
    }

    private void SavePrefs()
    {
        EditorPrefs.SetString(PrefKeyPrefix + "TargetFolder", _targetFolder ?? string.Empty);
        EditorPrefs.SetBool(PrefKeyPrefix + "KeepSubfolders", _keepSubfolders);
        EditorPrefs.SetBool(PrefKeyPrefix + "StripExt", _stripOriginalExtension);

        // 用 | 拼接：避免 EditorPrefs 的数组限制
        var joined = string.Join("|", _sourceFilePaths);
        EditorPrefs.SetString(PrefKeyPrefix + "SourceFiles", joined ?? string.Empty);
    }

    [MenuItem("Tools/HotUpdate/Move To .bytes (Copy)...")]
    private static void Open()
    {
        GetWindow<BytesSuffixMoverWindow>(true, "Move To .bytes", true);
    }

    private void OnGUI()
    {
        EditorGUI.BeginChangeCheck();

        EditorGUILayout.LabelField("源文件（可多选）", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("添加文件...", GUILayout.Height(22)))
                {
                    var picked = EditorUtility.OpenFilePanel("选择源文件", "", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        AddSourceFile(picked);
                        SavePrefs();
                    }
                }

                if (GUILayout.Button("添加文件夹...", GUILayout.Height(22)))
                {
                    var picked = EditorUtility.OpenFolderPanel("选择源文件夹", "", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        AddSourceFolder(picked);
                        SavePrefs();
                    }
                }

                if (GUILayout.Button("清空", GUILayout.Height(22), GUILayout.Width(60)))
                {
                    _sourceFilePaths.Clear();
                    _sources = Array.Empty<UnityEngine.Object>();
                    SavePrefs();
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField($"已添加文件：{_sourceFilePaths.Count}");
            using (var scroll = new EditorGUILayout.ScrollViewScope(Vector2.zero, GUILayout.Height(110)))
            {
                for (int i = 0; i < _sourceFilePaths.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.SelectableLabel(_sourceFilePaths[i], GUILayout.Height(18));
                        if (GUILayout.Button("X", GUILayout.Width(22)))
                        {
                            _sourceFilePaths.RemoveAt(i);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("（可选）从 Project 资源填充", EditorStyles.miniBoldLabel);
            var size = Mathf.Max(0, EditorGUILayout.IntField("Size", _sources?.Length ?? 0));
            if (_sources == null || _sources.Length != size)
                Array.Resize(ref _sources, size);

            if (_sources != null)
            {
                for (int i = 0; i < _sources.Length; i++)
                    _sources[i] = EditorGUILayout.ObjectField($"Project Source {i}", _sources[i], typeof(UnityEngine.Object), false);
            }

            if (GUILayout.Button("从 Project 选择填充", GUILayout.Height(22)))
            {
                _sources = Selection.objects;
            }
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("目标目录", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            _targetFolder = EditorGUILayout.TextField("Target Folder", _targetFolder);
            if (GUILayout.Button("选择...", GUILayout.Width(80)))
            {
                var picked = EditorUtility.OpenFolderPanel("选择目标目录", _targetFolder, "");
                if (!string.IsNullOrEmpty(picked))
                    _targetFolder = picked;
            }
        }

        _keepSubfolders = EditorGUILayout.ToggleLeft("保持相对目录结构（基于 Assets/）", _keepSubfolders);
        _stripOriginalExtension = EditorGUILayout.ToggleLeft("去掉原扩展名（a.json -> a.bytes）", _stripOriginalExtension);

        if (EditorGUI.EndChangeCheck())
            SavePrefs();

        EditorGUILayout.Space(12);

        using (new EditorGUI.DisabledScope(!CanRun(out _)))
        {
            if (GUILayout.Button("Copy && Rename To .bytes", GUILayout.Height(32)))
                Run();
        }

        if (!CanRun(out var reason))
            EditorGUILayout.HelpBox(reason, MessageType.Warning);

        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            "说明：会把源文件复制到目标目录，并在目标文件名后追加 .bytes（覆盖同名文件）。\n" +
            "- 默认：a.json -> a.json.bytes\n" +
            "- 勾选“去掉原扩展名”：a.json -> a.bytes",
            MessageType.Info);
    }

    private bool CanRun(out string reason)
    {
        var hasAnySource = (_sourceFilePaths.Count > 0) || (_sources != null && _sources.Length > 0);
        if (!hasAnySource)
        {
            reason = "请先添加源文件（支持：系统文件/文件夹 或 Project 资源）";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_targetFolder))
        {
            reason = "请先选择目标目录";
            return false;
        }

        reason = null;
        return true;
    }

    private void Run()
    {
        try
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var targetRoot = Path.GetFullPath(_targetFolder);

            if (!Directory.Exists(targetRoot))
                Directory.CreateDirectory(targetRoot);

            int copied = 0;
            int skipped = 0;

            // 1) 先处理系统选择文件
            foreach (var srcFullPath in _sourceFilePaths.ToArray())
            {
                if (string.IsNullOrWhiteSpace(srcFullPath) || !File.Exists(srcFullPath))
                {
                    skipped++;
                    continue;
                }

                var outDir = targetRoot;
                var fileName = Path.GetFileName(srcFullPath);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                string outName;
                if (_stripOriginalExtension)
                    outName = nameWithoutExt + ".bytes";
                else
                    outName = fileName + ".bytes";

                var dstFullPath = Path.Combine(outDir, outName);
                File.Copy(srcFullPath, dstFullPath, true);
                copied++;
            }

            // 2) 再处理 Project 资源
            if (_sources != null)
            {
                foreach (var obj in _sources)
                {
                    if (obj == null)
                    {
                        skipped++;
                        continue;
                    }

                    var assetPath = AssetDatabase.GetAssetPath(obj);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        Debug.LogWarning($"[BytesSuffixMover] 跳过：无法获取资源路径：{obj.name}");
                        skipped++;
                        continue;
                    }

                    var srcFullPath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
                    if (!File.Exists(srcFullPath))
                    {
                        Debug.LogWarning($"[BytesSuffixMover] 跳过：源文件不存在：{srcFullPath}");
                        skipped++;
                        continue;
                    }

                    string relativeDir = "";
                    if (_keepSubfolders)
                    {
                        var assetDir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(assetDir) && assetDir.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                            relativeDir = assetDir.Substring("Assets".Length).TrimStart('/', '\\');
                    }

                    var fileName = Path.GetFileName(srcFullPath);
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                    string outName;
                    if (_stripOriginalExtension)
                        outName = nameWithoutExt + ".bytes";
                    else
                        outName = fileName + ".bytes";

                    var outDir = string.IsNullOrEmpty(relativeDir) ? targetRoot : Path.Combine(targetRoot, relativeDir);
                    if (!Directory.Exists(outDir))
                        Directory.CreateDirectory(outDir);

                    var dstFullPath = Path.Combine(outDir, outName);
                    File.Copy(srcFullPath, dstFullPath, true);
                    copied++;
                }
            }

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Done", $"复制完成。copied={copied}, skipped={skipped}\n目标：{targetRoot}", "OK");
            Debug.Log($"[BytesSuffixMover] Done. copied={copied} skipped={skipped} target={targetRoot}");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorUtility.DisplayDialog("Failed", e.Message, "OK");
        }
    }

    private void AddSourceFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        if (File.Exists(full) && !_sourceFilePaths.Contains(full))
            _sourceFilePaths.Add(full);
    }

    private void AddSourceFolder(string folderPath)
    {
        var full = Path.GetFullPath(folderPath);
        if (!Directory.Exists(full))
            return;

        foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
            AddSourceFile(file);
    }
}
#endif
