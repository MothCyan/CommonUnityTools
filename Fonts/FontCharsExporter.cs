#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FontCharsExporter
{
    private const string DefaultOutputPath = "Assets/FontChars.txt";
    private const string DefaultCodePointsPath = "Assets/FontChars_CodePoints.txt";
    private const string DefaultSourcesPath = "Assets/FontChars_Sources.txt";
    private const string DefaultSkillDataDebugPath = "Assets/FontChars_SkillData_Debug.txt";

    // 导出开关：默认更“干净”，避免把整篇文档/代码的字符全扫进来
    private static bool IncludeCSharpStringLiterals = false;
    private static bool IncludeTextAssetsByExtension = true; // 统计表/配置里的文本（默认开启）

    // 可选：只扫描这些目录下的文本资源（为空则扫描整个 Assets）
    // 注意：你项目的 DataTables 目录在项目根目录（不在 Assets 下）。
    private static readonly string[] TextAssetIncludeFolders =
    {
        "Assets/Resources",
        "Assets/StreamingAssets",
        "Assets/StreamingAssets/yoo", // YooAsset Catalog 配置
        "DataTables", // 项目根目录下的表
    };

    // 你可以按需增删后缀（补充常见表格/本地化/二进制转文本等）
    private static readonly string[] TextAssetExtensions =
    {
        ".json", ".txt", ".csv", ".tsv", ".lua", ".yaml", ".yml", ".xml", ".md",
        ".bytes", ".loc", ".lang"
    };

    // 长文本过滤：如果一个字符串里出现很长的连续英文/数字段，通常是代码/文档/表格，
    // 这会把字体字符集膨胀得很离谱。达到阈值则跳过该字符串。
    private const int SkipIfHasLongAsciiRun = 40;

    // 过滤策略：只有当字符串本身很长时，才认为它可能是文档/代码并应用过滤。
    private const int MinStringLengthToFilter = 120;

    // 溯源统计：记录每个来源新增了多少字符，便于定位“是谁塞进来大量字”
    private sealed class SourceStat
    {
        public int strings;
        public int newChars;
    }

    // 临时排查开关：用于确认 SkillData.description 是否真的被扫到
    private static bool DebugLogSkillDataStrings = false;

    [MenuItem("Tools/Fonts/导出项目用到的所有字(生成FontChars.txt)")]
    public static void ExportAllChars()
    {
        try
        {
            var set = new SortedSet<int>();
            var stats = new Dictionary<string, SourceStat>(StringComparer.Ordinal);

            CollectFromTmpTextsInProject(set, stats);
            CollectFromScriptableObjects(set, stats);
            CollectFromSkillDataAssets(set, stats); // 强制兜底：技能文案

            if (IncludeCSharpStringLiterals)
                CollectFromCSharpSource(set, stats);

            if (IncludeTextAssetsByExtension)
                CollectFromTextAssets(set, stats);

            // fallback：用带来源统计的方式加入（避免污染/并能统计新增字符数）
            AddString(set, "0123456789", "Fallback", stats);
            AddString(set, "abcdefghijklmnopqrstuvwxyz", "Fallback", stats);
            AddString(set, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "Fallback", stats);
            AddString(set, " ,.!?;:'\"-_/\\()[]{}<>@#$%^&*+=~`|\n\r\t", "Fallback", stats);

            string chars = BuildStringFromCodePoints(set);
            WriteToFile(DefaultOutputPath, chars);

            string codePoints = BuildCodePointsReport(set);
            WriteToFile(DefaultCodePointsPath, codePoints);

            string sources = BuildSourcesReport(stats);
            WriteToFile(DefaultSourcesPath, sources);

            Debug.Log($"[FontCharsExporter] 导出完成：{DefaultOutputPath}，字符数={set.Count}；码点清单：{DefaultCodePointsPath}；来源：{DefaultSourcesPath}；SkillData调试：{DefaultSkillDataDebugPath}。" +
                      $"(IncludeCSharp={IncludeCSharpStringLiterals}, IncludeTextAssets={IncludeTextAssetsByExtension})");
        }
        catch (Exception e)
        {
            Debug.LogError($"[FontCharsExporter] 导出失败：{e}");
        }
    }

    private static void CollectFromTmpTextsInProject(SortedSet<int> set, Dictionary<string, SourceStat> stats)
    {
        // 扫描所有 prefab（包括 UI）
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            foreach (var tmp in prefab.GetComponentsInChildren<TMP_Text>(true))
            {
                if (tmp == null) continue;
                string source = $"Prefab:{path} | {GetHierarchyPath(tmp.gameObject)}";
                AddString(set, tmp.text, source, stats);
            }
        }

        // 扫描所有场景里的 TMP 文本（为了覆盖一些非prefab的场景文本）
        string[] sceneGuids = AssetDatabase.FindAssets("t:Scene");
        foreach (string guid in sceneGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) continue;
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;

            SceneAsset sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (sceneAsset == null) continue;

            UnityEngine.SceneManagement.Scene scene = default;
            bool opened = false;
            try
            {
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                opened = scene.IsValid();
                if (!opened) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
                    {
                        if (tmp == null) continue;
                        string source = $"Scene:{path} | {GetHierarchyPath(tmp.gameObject)}";
                        AddString(set, tmp.text, source, stats);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FontCharsExporter] 跳过无法打开的场景：{path}\n{e.Message}");
            }
            finally
            {
                if (opened)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }
        }
    }

    private static void CollectFromScriptableObjects(SortedSet<int> set, Dictionary<string, SourceStat> stats)
    {
        string[] guids = AssetDatabase.FindAssets("t:ScriptableObject");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) continue;

            // 关键：一个 .asset 文件里可能包含多个 ScriptableObject 子资源
            // 仅 LoadAssetAtPath 可能拿不到你真正想要的那个子对象
            UnityEngine.Object[] assetsAtPath;
            try { assetsAtPath = AssetDatabase.LoadAllAssetsAtPath(path); }
            catch { continue; }

            if (assetsAtPath == null || assetsAtPath.Length == 0)
                continue;

            foreach (var obj in assetsAtPath)
            {
                if (obj == null) continue;
                if (obj is not ScriptableObject) continue;

                // SerializedObject 能枚举所有可序列化字段（包括私有[SerializeField]）
                SerializedObject so;
                try { so = new SerializedObject(obj); }
                catch { continue; }

                var it = so.GetIterator();

                // 用 Next(true) 而不是 NextVisible(true)：避免被“Inspector 不可见字段”漏掉
                bool enterChildren = true;
                while (it.Next(enterChildren))
                {
                    enterChildren = true;
                    if (it.propertyType != SerializedPropertyType.String)
                        continue;

                    string value;
                    try { value = it.stringValue; }
                    catch { continue; }

                    string source = $"SO:{path} | {obj.name} | {it.propertyPath}";
                    AddString(set, value, source, stats);

                    if (DebugLogSkillDataStrings && obj is SkillData)
                    {
                        // 只打很少的日志，避免刷屏
                        if (!string.IsNullOrEmpty(value) && value.Length <= 120)
                            Debug.Log($"[FontCharsExporter][SkillData] {path} ({obj.name}) {it.propertyPath} = {value}");
                    }
                }
            }
        }
    }

    private static void CollectFromCSharpSource(SortedSet<int> set, Dictionary<string, SourceStat> stats)
    {
        string assetsRoot = Application.dataPath;
        foreach (string file in Directory.EnumerateFiles(assetsRoot, "*.cs", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            foreach (var s in ExtractCSharpStringLiterals(text))
            {
                string source = $"CS:{ToProjectRelativePath(file)}";
                AddString(set, s, source, stats);
            }
        }
    }

    private static void CollectFromTextAssets(SortedSet<int> set, Dictionary<string, SourceStat> stats)
    {
        // 不仅扫 Assets，还扫项目根目录下的 DataTables（luban 导出常在这里）
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string assetsRoot = Application.dataPath;

        // 目录白名单：把路径转成绝对路径（同时支持 Assets 内与项目根目录下的目录）
        var includeAbs = new List<string>();
        if (TextAssetIncludeFolders != null)
        {
            foreach (var f in TextAssetIncludeFolders)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                string full = Path.GetFullPath(Path.Combine(projectRoot, f));
                includeAbs.Add(full);
            }
        }

        // 搜索根目录：白名单不为空时，只枚举这些目录；否则默认枚举整个 Assets
        IEnumerable<string> roots;
        if (includeAbs.Count > 0)
        {
            var list = new List<string>();
            foreach (var r in includeAbs)
            {
                if (Directory.Exists(r)) list.Add(r);
            }
            roots = list;
        }
        else
        {
            roots = new[] { assetsRoot };
        }

        foreach (var root in roots)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (string.IsNullOrEmpty(ext)) continue;

                bool match = false;
                foreach (var e in TextAssetExtensions)
                {
                    if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                }
                if (!match) continue;

                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }

                string source = $"Text:{ToProjectRelativePath(file)}";
                AddString(set, text, source, stats);
            }
        }
    }

    private static void CollectFromSkillDataAssets(SortedSet<int> set, Dictionary<string, SourceStat> stats)
    {
        // 目的：
        // 1) 明确项目里到底有多少 SkillData 资产（很多项目其实没有 .asset，全靠表/运行时生成）
        // 2) 就算 ScriptableObject 全扫漏了，这里也要把技能名/描述强制加进字符集

        string[] guids;
        try { guids = AssetDatabase.FindAssets($"t:{nameof(SkillData)}"); }
        catch
        {
            // 如果类型没被 Unity 识别（极少见），直接返回
            return;
        }

        var sb = new StringBuilder(256);
        sb.AppendLine("# SkillData debug");
        sb.AppendLine($"Count={guids?.Length ?? 0}");

        if (guids == null || guids.Length == 0)
        {
            sb.AppendLine("No SkillData assets found by AssetDatabase.FindAssets(t:SkillData).");
            WriteToFile(DefaultSkillDataDebugPath, sb.ToString());
            return;
        }

        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) continue;

            SkillData sd = AssetDatabase.LoadAssetAtPath<SkillData>(path);
            if (sd == null)
            {
                sb.AppendLine($"- {path} : LoadAssetAtPath<SkillData> = null");
                continue;
            }

            // 直接读字段（不走 SerializedObject），排除“SerializedObject 读不到”的可能
            string name = sd.skillName;
            string desc = sd.description;

            sb.AppendLine($"- {path} | assetName={sd.name} | skillNameLen={(name?.Length ?? 0)} | descLen={(desc?.Length ?? 0)}");
            if (!string.IsNullOrEmpty(name)) sb.AppendLine($"  skillName: {name}");
            if (!string.IsNullOrEmpty(desc)) sb.AppendLine($"  description: {desc}");

            AddString(set, name, $"SkillData:{path} | skillName", stats);
            AddString(set, desc, $"SkillData:{path} | description", stats);
        }

        WriteToFile(DefaultSkillDataDebugPath, sb.ToString());
    }

    private static string BuildSourcesReport(Dictionary<string, SourceStat> stats)
    {
        var list = new List<KeyValuePair<string, SourceStat>>(stats);
        list.Sort((a, b) => b.Value.newChars.CompareTo(a.Value.newChars));

        var sb = new StringBuilder(list.Count * 64);
        sb.AppendLine("# FontCharsExporter sources");
        sb.AppendLine("# Columns: NewChars\tStrings\tSource");

        foreach (var kv in list)
            sb.AppendLine($"{kv.Value.newChars}\t{kv.Value.strings}\t{kv.Key}");

        return sb.ToString();
    }

    private static string GetHierarchyPath(GameObject go)
    {
        if (go == null) return string.Empty;
        var names = new List<string>(8);
        Transform t = go.transform;
        while (t != null)
        {
            names.Add(t.name);
            t = t.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static string ToProjectRelativePath(string fullPath)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        fullPath = Path.GetFullPath(fullPath);
        projectRoot = projectRoot.Replace('\\', '/');
        fullPath = fullPath.Replace('\\', '/');
        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            return fullPath;
        string rel = fullPath.Substring(projectRoot.Length);
        if (rel.StartsWith("/")) rel = rel.Substring(1);
        return rel;
    }

    // 兼容旧调用：保留无来源版本，但内部走带来源版本
    private static void AddString(SortedSet<int> set, string s)
    {
        AddString(set, s, "(unknown)", null);
    }

    private static void AddString(SortedSet<int> set, string s, string source, Dictionary<string, SourceStat> stats)
    {
        if (string.IsNullOrEmpty(s)) return;

        if (s.Length >= MinStringLengthToFilter && HasLongAsciiRun(s, SkipIfHasLongAsciiRun))
            return;

        s = StripRichTextTags(s);

        int before = set.Count;

        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.ConvertToUtf32(s, i);
            set.Add(cp);
            if (char.IsSurrogatePair(s, i)) i++;
        }

        if (stats != null)
        {
            int added = set.Count - before;
            if (!stats.TryGetValue(source, out var st))
            {
                st = new SourceStat();
                stats[source] = st;
            }
            st.strings++;
            st.newChars += Math.Max(0, added);
        }
    }

    private static string BuildStringFromCodePoints(SortedSet<int> set)
    {
        var sb = new StringBuilder(set.Count);
        foreach (int cp in set)
        {
            // 仅跳过不可用的控制字符。
            // 保留：\t(9) \n(10) \r(13)
            if (cp < 32 && cp != 9 && cp != 10 && cp != 13)
                continue;

            // 过滤 Unicode 代理码点本身（避免异常）
            if (cp >= 0xD800 && cp <= 0xDFFF)
                continue;

            sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    private static string BuildCodePointsReport(SortedSet<int> set)
    {
        var sb = new StringBuilder(set.Count * 8);
        sb.AppendLine("# FontCharsExporter code points");
        sb.AppendLine("# Format: U+XXXX<TAB>char");

        foreach (int cp in set)
        {
            if (cp < 32 && cp != 9 && cp != 10 && cp != 13)
                continue;
            if (cp >= 0xD800 && cp <= 0xDFFF)
                continue;

            string ch = cp == 9 ? "\\t" : (cp == 10 ? "\\n" : (cp == 13 ? "\\r" : char.ConvertFromUtf32(cp)));
            sb.Append("U+");
            sb.Append(cp.ToString("X4"));
            sb.Append('\t');
            sb.AppendLine(ch);
        }

        return sb.ToString();
    }

    private static void WriteToFile(string assetPath, string content)
    {
        string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? string.Empty);
        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        AssetDatabase.Refresh();
    }

    private static string StripRichTextTags(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // 简易去标签：<...>
        return Regex.Replace(input, "<[^>]+>", string.Empty);
    }

    private static bool HasLongAsciiRun(string s, int threshold)
    {
        int run = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool isAsciiWord = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
            if (isAsciiWord)
            {
                run++;
                if (run >= threshold) return true;
            }
            else
            {
                run = 0;
            }
        }
        return false;
    }

    private static IEnumerable<string> ExtractCSharpStringLiterals(string code)
    {
        // 说明：这里实现的是“够用”的提取器，不是完整 C# 语法解析器。
        // - 支持普通字符串："..."（处理常见转义）
        // - 支持逐字字符串：@"..."（双引号用 "" 逃逸）

        // 逐字字符串 @"..."
        foreach (Match m in Regex.Matches(code, "@\"(?:\"\"|[^\"])*\"", RegexOptions.Singleline))
        {
            string raw = m.Value;
            // 去掉开头 @" 和结尾 "
            string inner = raw.Substring(2, raw.Length - 3);
            inner = inner.Replace("\"\"", "\"");
            yield return inner;
        }

        // 普通字符串 "..."
        foreach (Match m in Regex.Matches(code, "(?<!@)\"(?:\\\\.|[^\\\"])*\"", RegexOptions.Singleline))
        {
            string raw = m.Value;
            string inner = raw.Substring(1, raw.Length - 2);
            // 反转义常用序列
            inner = inner
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
            yield return inner;
        }
    }
}
#endif
