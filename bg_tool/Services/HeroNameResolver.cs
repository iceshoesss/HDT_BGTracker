using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

#nullable enable

namespace BgTool
{

/// <summary>
/// 通过内置 JSON 文件将 heroCardId 解析为中文英雄名
/// 替代 HearthDb.dll，零外部依赖
/// </summary>
public static class HeroNameResolver
{
    private static bool _initialized;
    private static Dictionary<string, string> _map = new Dictionary<string, string>();

    /// <summary>
    /// 解析 heroCardId → 中文英雄名，失败返回原 cardId
    /// </summary>
    public static string Resolve(string heroCardId)
    {
        if (string.IsNullOrEmpty(heroCardId)) return "";

        if (!_initialized)
        {
            _initialized = true;
            LoadFromEmbeddedJson();
        }

        if (_map.TryGetValue(heroCardId, out var name) && !string.IsNullOrEmpty(name))
            return name;

        return heroCardId;
    }

    private static void LoadFromEmbeddedJson()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // 尝试从嵌入资源读取
            var resourceName = FindResource(asm, "bg_heroes.json");
            if (resourceName != null)
            {
                using (var stream = asm.GetManifestResourceStream(resourceName))
                using (var reader = new StreamReader(stream))
                {
                    var json = reader.ReadToEnd();
                    _map = ParseSimpleJson(json);
                    Console.WriteLine($"[HeroNameResolver] ✅ 已加载 {_map.Count} 个英雄名（嵌入资源）");
                    return;
                }
            }

            // 回退：从 exe 同目录读取
            var exeDir = Path.GetDirectoryName(asm.Location);
            if (exeDir != null)
            {
                var filePath = Path.Combine(exeDir, "bg_heroes.json");
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    _map = ParseSimpleJson(json);
                    Console.WriteLine($"[HeroNameResolver] ✅ 已加载 {_map.Count} 个英雄名（本地文件）");
                    return;
                }
            }

            Console.WriteLine("[HeroNameResolver] ⚠️ 未找到 bg_heroes.json，英雄名将显示 cardId");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[HeroNameResolver] ⚠️ 加载失败: {e.Message}");
        }
    }

    private static string? FindResource(Assembly asm, string fileName)
    {
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    /// <summary>
    /// 极简 JSON 解析：只处理 {"key":"value", ...} 格式，不依赖第三方库
    /// </summary>
    private static Dictionary<string, string> ParseSimpleJson(string json)
    {
        var result = new Dictionary<string, string>();
        int i = 0;
        int len = json.Length;

        // Skip whitespace
        SkipWs(ref i, json, len);
        if (i >= len || json[i] != '{') return result;
        i++; // skip {

        while (i < len)
        {
            SkipWs(ref i, json, len);
            if (i >= len || json[i] == '}') break;

            // Read key
            var key = ReadString(ref i, json, len);
            if (key == null) break;

            SkipWs(ref i, json, len);
            if (i < len && json[i] == ':') i++; // skip :
            SkipWs(ref i, json, len);

            // Read value
            var value = ReadString(ref i, json, len);
            if (value == null) break;

            result[key] = value;

            SkipWs(ref i, json, len);
            if (i < len && json[i] == ',') i++; // skip ,
        }

        return result;
    }

    private static void SkipWs(ref int i, string json, int len)
    {
        while (i < len && char.IsWhiteSpace(json[i])) i++;
    }

    private static string? ReadString(ref int i, string json, int len)
    {
        if (i >= len || json[i] != '"') return null;
        i++; // skip opening "

        var start = i;
        while (i < len)
        {
            if (json[i] == '\\')
            {
                i += 2; // skip escaped char
                continue;
            }
            if (json[i] == '"')
            {
                var raw = json.Substring(start, i - start);
                i++; // skip closing "
                // Handle basic escape sequences
                raw = raw.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
                return raw;
            }
            i++;
        }
        return null;
    }
}

}
