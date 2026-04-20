using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

#nullable enable

namespace BgTool
{

/// <summary>
/// Flask API 客户端（check-league + update-placement）
/// </summary>
public static class ApiClient
{
    // 配置
    private static string _baseUrl = "";
    private static string _apiKey = "";
    private static string _pluginVersion = "0.5.7"; // 服务端兼容版本，bg_tool 实际版本另算

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    // 状态
    public static string VerificationCode { get; private set; } = "";
    public static bool LastLeagueResult { get; private set; }
    public static string LastError { get; private set; } = "";

    /// <summary>
    /// 初始化配置（从配置文件或默认值）
    /// </summary>
    public static void Init(string baseUrl, string apiKey = "")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    /// <summary>
    /// 英雄选定后调用，检查是否为联赛对局
    /// </summary>
    public static async Task<bool> CheckLeagueAsync(
        string gameUuid,
        string playerId,
        ulong accountIdLo,
        List<LobbyPlayer> lobbyPlayers,
        string region = "CN",
        string mode = "solo")
    {
        LastError = "";

        // 构建 accountIdLoList
        var loList = new List<string>();
        var playersDict = new Dictionary<string, object>();

        foreach (var p in lobbyPlayers)
        {
            if (p.Lo == 0) continue;
            var loStr = p.Lo.ToString();
            loList.Add(loStr);
            playersDict[loStr] = new
            {
                heroCardId = p.HeroCardId ?? ""
            };
        }

        var body = new Dictionary<string, object>
        {
            ["gameUuid"] = gameUuid,
            ["accountIdLoList"] = loList,
            ["playerId"] = playerId,
            ["accountIdLo"] = accountIdLo.ToString(),
            ["players"] = playersDict,
            ["mode"] = mode,
            ["region"] = region
        };

        try
        {
            var (ok, json) = await PostAsync("/api/plugin/check-league", body);
            if (!ok) return false;

            // 解析响应
            var isLeague = json.Contains("\"isLeague\"") && json.Contains("true");
            if (isLeague)
            {
                VerificationCode = ExtractJsonString(json, "verificationCode");
                Console.WriteLine($"[API] ✅ 联赛对局 | 验证码: {VerificationCode}");
            }
            else
            {
                Console.WriteLine("[API] 非联赛对局，跳过");
            }

            LastLeagueResult = isLeague;
            return isLeague;
        }
        catch (Exception e)
        {
            LastError = $"check-league 异常: {e.Message}";
            Console.WriteLine($"[API] ⚠️ {LastError}");
            return false;
        }
    }

    /// <summary>
    /// 游戏结束时调用，上报排名
    /// </summary>
    public static async Task<bool> UpdatePlacementAsync(
        string gameUuid,
        string playerId,
        ulong accountIdLo,
        int placement)
    {
        LastError = "";

        var body = new Dictionary<string, object>
        {
            ["gameUuid"] = gameUuid,
            ["accountIdLo"] = accountIdLo.ToString(),
            ["placement"] = placement,
            ["playerId"] = playerId
        };

        // 重试 3 次
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var (ok, json) = await PostAsync("/api/plugin/update-placement", body);
                if (!ok)
                {
                    if (attempt < 3)
                    {
                        Console.WriteLine($"[API] update-placement 失败，第 {attempt} 次重试...");
                        await Task.Delay(2000);
                        continue;
                    }
                    return false;
                }

                var finalized = json.Contains("\"finalized\"") && json.Contains("true");
                Console.WriteLine($"[API] ✅ 排名已上传: 第 {placement} 名 | finalized={finalized}");
                return true;
            }
            catch (Exception e)
            {
                if (attempt < 3)
                {
                    Console.WriteLine($"[API] update-placement 异常，第 {attempt} 次重试: {e.Message}");
                    await Task.Delay(2000);
                    continue;
                }
                LastError = $"update-placement 异常: {e.Message}";
                Console.WriteLine($"[API] ⚠️ {LastError}");
                return false;
            }
        }
        return false;
    }

    // ── HTTP 工具方法 ──

    private static async Task<(bool ok, string json)> PostAsync(string path, Dictionary<string, object> body)
    {
        var url = _baseUrl + path;
        var jsonBody = SimpleJsonSerialize(body);
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("X-HDT-Plugin", _pluginVersion);
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

        var response = await _http.SendAsync(request);
        var respBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[API] ❌ {path} → {(int)response.StatusCode} {respBody}");
            return (false, respBody);
        }

        return (true, respBody);
    }

    // ── 极简 JSON 序列化（避免依赖第三方库） ──

    private static string SimpleJsonSerialize(Dictionary<string, object> dict)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var kv in dict)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(kv.Key).Append('"').Append(':');
            AppendJsonValue(sb, kv.Value);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendJsonValue(StringBuilder sb, object? val)
    {
        switch (val)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int i:
                sb.Append(i);
                break;
            case long l:
                sb.Append(l);
                break;
            case double d:
                sb.Append(d);
                break;
            case string s:
                sb.Append('"').Append(EscapeJsonString(s)).Append('"');
                break;
            case List<string> list:
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJsonString(list[i])).Append('"');
                }
                sb.Append(']');
                break;
            case Dictionary<string, object> sub:
                sb.Append(SimpleJsonSerialize(sub));
                break;
            default:
                sb.Append('"').Append(EscapeJsonString(val.ToString() ?? "")).Append('"');
                break;
        }
    }

    private static string EscapeJsonString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    private static string ExtractJsonString(string json, string key)
    {
        var search = $"\"{key}\"";
        var idx = json.IndexOf(search);
        if (idx < 0) return "";
        idx += search.Length;
        // 跳过 ": "
        idx = json.IndexOf('"', idx);
        if (idx < 0) return "";
        idx++; // 跳过开始引号
        var end = json.IndexOf('"', idx);
        if (end < 0) return "";
        return json.Substring(idx, end - idx);
    }
}
}
