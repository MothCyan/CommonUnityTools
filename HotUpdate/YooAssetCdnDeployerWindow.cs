#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// YooAsset CDN 部署工具：
/// - 把 Bundles/<平台>/<packageName> 下的内容复制到 CDN 目录
/// - 支持“覆盖同名文件”(Force)
/// - 支持仅复制最新版本目录
///
/// 说明：
/// - 该工具仅做文件拷贝，不负责生成 Bundle。
/// - 默认包名按项目常见的 MyPackage。
/// </summary>
public sealed class YooAssetCdnDeployerWindow : EditorWindow
{
    private enum CopyMode
    {
        CopyAll,
        CopyLatestVersionOnly
    }

    private string _platform = "WebGL";
    private string _packageName = "MyPackage";

    // 目标目录（最终复制到这里）
    private string _targetPath = "";

    private CopyMode _copyMode = CopyMode.CopyAll;
    private bool _overwrite = true;
    private bool _deleteExtraFilesInTarget = false;

    private const string PrefKeyPrefix = "WxGame.YooAssetCdnDeployer.";

    private const int CopyRetryCount = 6;
    private const int CopyRetryDelayMs = 200;

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
        _platform = EditorPrefs.GetString(PrefKeyPrefix + "Platform", _platform);
        _packageName = EditorPrefs.GetString(PrefKeyPrefix + "Package", _packageName);
        _targetPath = EditorPrefs.GetString(PrefKeyPrefix + "TargetPath", _targetPath);
        _copyMode = (CopyMode)EditorPrefs.GetInt(PrefKeyPrefix + "CopyMode", (int)_copyMode);
        _overwrite = EditorPrefs.GetBool(PrefKeyPrefix + "Overwrite", _overwrite);
        _deleteExtraFilesInTarget = EditorPrefs.GetBool(PrefKeyPrefix + "DeleteExtra", _deleteExtraFilesInTarget);
    }

    private void SavePrefs()
    {
        EditorPrefs.SetString(PrefKeyPrefix + "Platform", _platform ?? string.Empty);
        EditorPrefs.SetString(PrefKeyPrefix + "Package", _packageName ?? string.Empty);
        EditorPrefs.SetString(PrefKeyPrefix + "TargetPath", _targetPath ?? string.Empty);
        EditorPrefs.SetInt(PrefKeyPrefix + "CopyMode", (int)_copyMode);
        EditorPrefs.SetBool(PrefKeyPrefix + "Overwrite", _overwrite);
        EditorPrefs.SetBool(PrefKeyPrefix + "DeleteExtra", _deleteExtraFilesInTarget);
    }

    [MenuItem("Tools/HotUpdate/Deploy Bundles To CDN...")]
    private static void Open()
    {
        GetWindow<YooAssetCdnDeployerWindow>(true, "YooAsset CDN Deployer", true);
    }

    private void OnGUI()
    {
        EditorGUI.BeginChangeCheck();

        EditorGUILayout.LabelField("源路径 (自动)", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField("Bundles Package Root", GetBundlesSourceRoot());
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("部署设置", EditorStyles.boldLabel);

        _platform = EditorGUILayout.TextField("Platform", _platform);
        _packageName = EditorGUILayout.TextField("Package", _packageName);

        using (new EditorGUILayout.HorizontalScope())
        {
            _targetPath = EditorGUILayout.TextField("Target Path", _targetPath);
            if (GUILayout.Button("选择...", GUILayout.Width(80)))
            {
                var picked = EditorUtility.OpenFolderPanel("选择目标目录", _targetPath, "");
                if (!string.IsNullOrEmpty(picked))
                    _targetPath = picked;
            }
        }

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField("Target Path (最终)", GetCdnTargetRoot());
        }

        _copyMode = (CopyMode)EditorGUILayout.EnumPopup("Copy Mode", _copyMode);
        _overwrite = EditorGUILayout.ToggleLeft("覆盖同名文件 (Overwrite)", _overwrite);
        _deleteExtraFilesInTarget = EditorGUILayout.ToggleLeft("删除目标多余文件 (危险)", _deleteExtraFilesInTarget);

        if (EditorGUI.EndChangeCheck())
            SavePrefs();

        EditorGUILayout.Space(8);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("打开目标目录", GUILayout.Width(120)))
            {
                var dst = GetCdnTargetRoot();
                if (!string.IsNullOrEmpty(dst))
                    EditorUtility.RevealInFinder(dst);
            }
        }

        EditorGUILayout.Space(12);

        using (new EditorGUI.DisabledScope(!CanDeploy(out _)))
        {
            if (GUILayout.Button("Deploy", GUILayout.Height(32)))
                Deploy();
        }

        if (!CanDeploy(out var reason))
        {
            EditorGUILayout.HelpBox(reason, MessageType.Warning);
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            "说明：会把 Bundles/<Platform>/<Package> 下的版本目录（排除 OutputCache）里的所有文件复制到 Target Path，并覆盖同名文件。\n" +
            "CopyLatestVersionOnly 只复制最新版本目录。\n" +
            "DeleteExtraFiles 会删除目标中源不存在的文件，慎用。",
            MessageType.Info);
    }

    private string GetBundlesSourceRoot()
    {
        // projectRoot/Bundles/<platform>/<packageName>
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(projectRoot, "Bundles", _platform, _packageName);
    }

    private string GetBundlesOutputRoot()
    {
        // projectRoot/Bundles/<platform>/<packageName>/Output
        return Path.Combine(GetBundlesSourceRoot(), "Output");
    }

    private string GetCdnTargetRoot()
    {
        return string.IsNullOrWhiteSpace(_targetPath) ? string.Empty : Path.GetFullPath(_targetPath);
    }

    private bool CanDeploy(out string reason)
    {
        if (string.IsNullOrWhiteSpace(_targetPath))
        {
            reason = "请先选择 Target Path（最终部署目录）";
            return false;
        }

        var src = GetBundlesSourceRoot();
        if (!Directory.Exists(src))
        {
            reason = $"源目录不存在（需要先构建 YooAsset）：{src}";
            return false;
        }

        // 必须至少有一个版本目录（排除 OutputCache）
        var hasVersionDir = Directory.GetDirectories(src)
            .Select(Path.GetFileName)
            .Any(n => !string.IsNullOrEmpty(n) &&
                      !string.Equals(n, "OutputCache", StringComparison.OrdinalIgnoreCase) &&
                      System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{4}-\d{2}-\d{2}-\d+"));

        if (!hasVersionDir)
        {
            reason = $"源目录下未找到版本目录（形如 yyyy-MM-dd-xxx）：{src}";
            return false;
        }

        reason = null;
        return true;
    }

    private void Deploy()
    {
        if (!CanDeploy(out var reason))
        {
            Debug.LogWarning($"[YooAssetCdnDeployer] {reason}");
            return;
        }

        var srcRoot = GetBundlesSourceRoot();
        var dstRoot = GetCdnTargetRoot();

        try
        {
            if (!Directory.Exists(dstRoot))
                Directory.CreateDirectory(dstRoot);

            var failed = new System.Collections.Generic.List<string>();

            if (_copyMode == CopyMode.CopyLatestVersionOnly)
            {
                var latest = FindLatestVersionDir(srcRoot);
                if (latest == null)
                {
                    EditorUtility.DisplayDialog("Deploy Failed", "未找到版本目录（形如 yyyy-MM-dd-xxx）", "OK");
                    return;
                }

                var latestPath = Path.Combine(srcRoot, latest);
                CopyDirectory(latestPath, dstRoot, _overwrite, failed);
            }
            else
            {
                // CopyAll：复制所有版本目录（排除 OutputCache）到目标根（合并覆盖）
                var versionDirs = Directory.GetDirectories(srcRoot)
                    .Where(d => !string.Equals(Path.GetFileName(d), "OutputCache", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var dir in versionDirs)
                    CopyDirectory(dir, dstRoot, _overwrite, failed);
            }

            if (_deleteExtraFilesInTarget)
            {
                // 这里 DeleteExtra 以 Output 为基准可能会误删（因为是合并复制），默认仍按 dstRoot 自身执行会更危险。
                // 仅保留原逻辑：CopyAll 时尝试按 OutputRoot 删除差异文件（但由于合并复制，建议慎用）
                if (_copyMode == CopyMode.CopyAll)
                    DeleteFilesNotInSource(srcRoot, dstRoot);
                else
                    Debug.LogWarning("[YooAssetCdnDeployer] DeleteExtraFiles 仅在 CopyAll 模式下执行，已跳过。");
            }

            AssetDatabase.Refresh();

            if (failed.Count > 0)
            {
                var preview = string.Join("\n", failed.Take(20));
                EditorUtility.DisplayDialog(
                    "Deploy Done (Partial)",
                    $"部署完成，但有 {failed.Count} 个文件复制失败（通常是被占用/正在写入）。\n\n" +
                    $"前 20 个：\n{preview}\n\n" +
                    "建议：先确保 YooAsset 构建/写入已结束、关闭占用源目录文件的进程，再点 Deploy 重试。",
                    "OK");

                Debug.LogWarning($"[YooAssetCdnDeployer] Deploy partial. failed={failed.Count}\n{string.Join("\n", failed)}");
                return;
            }

            EditorUtility.DisplayDialog("Deploy Done", $"已部署到：\n{dstRoot}", "OK");
            Debug.Log($"[YooAssetCdnDeployer] Deploy done. src={srcRoot} dst={dstRoot}");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorUtility.DisplayDialog("Deploy Failed", e.Message, "OK");
        }
    }

    private static string FindLatestVersionDir(string srcRoot)
    {
        // 版本目录：yyyy-MM-dd-数字
        var dirs = Directory.GetDirectories(srcRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{4}-\d{2}-\d{2}-\d+"))
            .OrderByDescending(n => n)
            .ToArray();

        return dirs.Length > 0 ? dirs[0] : null;
    }

    private static void CopyDirectory(string sourceDir, string targetDir, bool overwrite, System.Collections.Generic.List<string> failed)
    {
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var dst = Path.Combine(targetDir, name);

            try
            {
                CopyFileWithRetry(file, dst, overwrite);
            }
            catch (Exception e)
            {
                failed?.Add($"{file} -> {dst} ({e.GetType().Name}: {e.Message})");
            }
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            var dst = Path.Combine(targetDir, name);
            CopyDirectory(dir, dst, overwrite, failed);
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir, bool overwrite)
    {
        // 兼容旧调用点
        CopyDirectory(sourceDir, targetDir, overwrite, failed: null);
    }

    private static void CopyFileWithRetry(string sourceFile, string destFile, bool overwrite)
    {
        // 说明：源文件可能正在被 YooAsset 构建/写入占用，直接 File.Copy 会抛 IOException。
        // 这里做简单重试；并通过“复制到临时文件再替换”的方式尽量规避目标文件被占用导致的不完整覆盖。
        for (var attempt = 0; attempt <= CopyRetryCount; attempt++)
        {
            try
            {
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                var tmp = destFile + ".tmp";

                // 先复制到 tmp
                File.Copy(sourceFile, tmp, true);

                if (!overwrite && File.Exists(destFile))
                {
                    File.Delete(tmp);
                    return;
                }

                // 再原子替换
                if (File.Exists(destFile))
                    File.Delete(destFile);

                File.Move(tmp, destFile);
                return;
            }
            catch (IOException) when (attempt < CopyRetryCount)
            {
                System.Threading.Thread.Sleep(CopyRetryDelayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < CopyRetryCount)
            {
                System.Threading.Thread.Sleep(CopyRetryDelayMs);
            }
        }

        // 最后一次仍失败，抛出
        var finalTmp = destFile + ".tmp";
        if (File.Exists(finalTmp))
        {
            try { File.Delete(finalTmp); } catch { /* ignore */ }
        }

        // 直接再试一次，让异常冒泡包含最新信息
        File.Copy(sourceFile, destFile, overwrite);
    }

    private static void DeleteFilesNotInSource(string sourceRoot, string targetRoot)
    {
        // 删除 target 中 source 不存在的文件/目录
        foreach (var targetFile in Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories))
        {
            var rel = MakeRelativePath(targetRoot, targetFile);
            var srcFile = Path.Combine(sourceRoot, rel);
            if (!File.Exists(srcFile))
                File.Delete(targetFile);
        }

        // 先删子目录
        var targetDirs = Directory.GetDirectories(targetRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToArray();

        foreach (var d in targetDirs)
        {
            var rel = MakeRelativePath(targetRoot, d);
            var srcDir = Path.Combine(sourceRoot, rel);
            if (!Directory.Exists(srcDir) && Directory.Exists(d))
                Directory.Delete(d, true);
        }
    }

    private static string MakeRelativePath(string basePath, string fullPath)
    {
        basePath = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        fullPath = Path.GetFullPath(fullPath);

        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        return fullPath.Substring(basePath.Length);
    }
}
#endif
