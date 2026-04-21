using System;
using System.IO;

#nullable enable

namespace BgTool
{

/// <summary>
/// bg_tool 配置
/// 
/// 查找顺序：
/// 1. 环境变量 BGTRACKER_CONFIG 指定的路径
/// 2. 从 exe 目录向上逐级查找 shared_config.json（最多 5 级）
/// 3. exe 同目录的 config.json
/// 4. exe 同目录的 config.json.example
/// </summary>
public class Config
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5000";
    public string Region { get; set; } = "CN";
    public string Mode { get; set; } = "solo";
    public bool TestMode { get; set; } = false;

    public static Config Load()
    {
        // 1. 环境变量指定
        var envPath = Environment.GetEnvironmentVariable("BGTRACKER_CONFIG");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            Console.WriteLine($"[Config] 使用环境变量指定: {envPath}");
            return Parse(envPath);
        }

        // 2. 从 exe 目录向上查找 shared_config.json
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            var shared = Path.Combine(dir, "shared_config.json");
            if (File.Exists(shared))
            {
                Console.WriteLine($"[Config] 找到共享配置: {shared}");
                return Parse(shared);
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        // 3. exe 同目录 config.json
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(exeDir, "config.json");
        if (File.Exists(local))
        {
            Console.WriteLine($"[Config] 使用本地配置: {local}");
            return Parse(local);
        }

        // 4. config.json.example
        var example = Path.Combine(exeDir, "config.json.example");
        if (File.Exists(example))
        {
            Console.WriteLine($"[Config] config.json 不存在，使用 example");
            return Parse(example);
        }

        Console.WriteLine("[Config] 未找到任何配置文件，使用默认值");
        return new Config();
    }

    private static Config Parse(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var cfg = new Config();

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"")) continue;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx < 0) continue;

                var key = trimmed.Substring(1, trimmed.IndexOf('"', 1) - 1).Trim();
                var valPart = trimmed.Substring(colonIdx + 1).Trim().TrimEnd(',');
                var val = valPart.Trim('"');

                switch (key)
                {
                    case "apiBaseUrl": cfg.ApiBaseUrl = val; break;
                    case "region": cfg.Region = val; break;
                    case "mode": cfg.Mode = val; break;
                    case "testMode": cfg.TestMode = val.Trim().ToLower() == "true"; break;
                }
            }

            Console.WriteLine($"[Config] apiBaseUrl={cfg.ApiBaseUrl}, region={cfg.Region}, mode={cfg.Mode}");
            return cfg;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Config] 解析失败: {e.Message}，使用默认值");
            return new Config();
        }
    }
}
}
