using System;
using System.IO;

#nullable enable

namespace BgTool
{

/// <summary>
/// bg_tool 配置（从 config.json 读取）
/// </summary>
public class Config
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = "";
    public string Region { get; set; } = "CN";
    public string Mode { get; set; } = "solo";

    /// <summary>
    /// 从 exe 同目录的 config.json 读取，不存在则返回默认值
    /// </summary>
    public static Config Load()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(exeDir, "config.json");

        if (!File.Exists(path))
        {
            Console.WriteLine($"[Config] config.json 不存在，使用默认配置");
            return new Config();
        }

        try
        {
            var text = File.ReadAllText(path);
            var cfg = new Config();

            // 极简 JSON 解析，只处理 string 字段
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("\""))
                {
                    var colonIdx = trimmed.IndexOf(':');
                    if (colonIdx < 0) continue;
                    var key = trimmed.Substring(1, trimmed.IndexOf('"', 1) - 1).Trim();
                    var valPart = trimmed.Substring(colonIdx + 1).Trim().TrimEnd(',');
                    var val = valPart.Trim('"');

                    switch (key)
                    {
                        case "apiBaseUrl": cfg.ApiBaseUrl = val; break;
                        case "apiKey": cfg.ApiKey = val; break;
                        case "region": cfg.Region = val; break;
                        case "mode": cfg.Mode = val; break;
                    }
                }
            }

            Console.WriteLine($"[Config] apiBaseUrl={cfg.ApiBaseUrl}, region={cfg.Region}, mode={cfg.Mode}");
            return cfg;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Config] 读取失败: {e.Message}，使用默认配置");
            return new Config();
        }
    }
}
}
