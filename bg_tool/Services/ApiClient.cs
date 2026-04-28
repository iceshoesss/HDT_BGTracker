using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

    // API Key 编译时写入，不暴露给用户配置
    // 发布前需替换为实际 Key
    private const string ApiKey = "";

    private static string _pluginVersion = "0.7.0"; // 服务端兼容版本，bg_tool 实际版本另算

    static ApiClient()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
    }

    private static readonly HttpClient _http = new HttpClient(
        new HttpClientHandler { UseProxy = false }
    ) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// 用 Lo 集合生成确定性 UUID（同一局游戏的 8 个 Lo → 同一个 UUID）
    /// </summary>
    public static string GenerateDeterministicUuid(List<LobbyPlayer> lobbyPlayers)
    {
        var los = lobbyPlayers
            .Where(p => p.Lo != 0)
            .Select(p => p.Lo.ToString())
            .OrderBy(s => s)
            .ToList();
        var input = string.Join(",", los);
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            // 取前 16 字节，按 UUID 格式输出
            return string.Format("{0:x2}{1:x2}{2:x2}{3:x2}-{4:x2}{5:x2}-{6:x2}{7:x2}-{8:x2}{9:x2}-{10:x2}{11:x2}{12:x2}{13:x2}{14:x2}{15:x2}",
                hash[0], hash[1], hash[2], hash[3],
                hash[4], hash[5],
                hash[6], hash[7],
                hash[8], hash[9],
                hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]);
        }
    }

    // 状态
    public static string VerificationCode { get; private set; } = "";
    public static bool LastLeagueResult { get; private set; }
    public static string LastError { get; private set; } = "";
    public static string ServerGameUuid { get; private set; } = "";  // 服务端返回的 gameUuid

    /// <summary>
    /// 初始化配置（从配置文件或默认值）
    /// </summary>
    public static void Init(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// 测试与服务器的连通性
    /// </summary>
    public static async Task<bool> PingAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/");
            request.Headers.Add("X-HDT-Plugin", _pluginVersion);
            if (!string.IsNullOrEmpty(ApiKey))
                request.Headers.Add("Authorization", $"Bearer {ApiKey}");

            var response = await _http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 英雄选定后调用，检查是否为联赛对局
    /// </summary>
    public static async Task<bool> CheckLeagueAsync(
        string playerId,
        ulong accountIdLo,
        List<LobbyPlayer> lobbyPlayers,
        string region = "CN",
        string mode = "solo",
        string startedAt = "")
    {
        LastError = "";
        ServerGameUuid = "";

        // Lo 全为 0 时跳过（HearthMirror 未就绪）
        var validPlayers = lobbyPlayers.Where(p => p.Lo != 0).ToList();
        if (validPlayers.Count == 0)
        {
            Console.WriteLine("[API] 所有 accountIdLo 为 0（LobbyInfo 未就绪），跳过 check-league");
            return false;
        }

        // 构建 accountIdLoList
        var loList = new List<string>();
        var playersDict = new Dictionary<string, object>();

        foreach (var p in lobbyPlayers)
        {
            var loStr = p.Lo.ToString();
            loList.Add(loStr);
            var playerInfo = new Dictionary<string, object>
            {
                ["heroCardId"] = p.HeroCardId ?? ""
            };
            if (!string.IsNullOrEmpty(p.HeroName))
                playerInfo["heroName"] = p.HeroName;
            // 本地玩家带 battleTag + displayName（HearthMirror 只有本地玩家有名字）
            if (p.Lo == accountIdLo)
            {
                if (!string.IsNullOrEmpty(playerId))
                    playerInfo["battleTag"] = playerId;
                var displayName = playerId;
                var hashIdx = playerId.IndexOf('#');
                if (hashIdx > 0)
                    displayName = playerId.Substring(0, hashIdx);
                if (!string.IsNullOrEmpty(displayName))
                    playerInfo["displayName"] = displayName;
            }
            playersDict[loStr] = playerInfo;
        }

        var body = new Dictionary<string, object>
        {
            ["accountIdLoList"] = loList,
            ["playerId"] = playerId,
            ["accountIdLo"] = accountIdLo.ToString(),
            ["players"] = playersDict,
            ["mode"] = mode,
            ["region"] = region,
            ["startedAt"] = startedAt ?? ""
        };

        try
        {
            var (ok, json) = await PostAsync("/api/plugin/check-league", body);
            if (!ok) throw new Exception($"HTTP 请求失败: {LastError}");

            // 解析响应
            // 无论 isLeague 结果如何，都提取 verificationCode
            // 服务端 check-league 总是返回 verificationCode（确保玩家在 player_records 中有记录）
            var vc = ExtractJsonString(json, "verificationCode");
            if (!string.IsNullOrEmpty(vc))
            {
                VerificationCode = vc;
                Console.WriteLine($"[API] ✅ 验证码: {VerificationCode}");
            }

            // 提取服务端返回的 gameUuid（淘汰赛由服务端生成）
            var serverUuid = ExtractJsonString(json, "gameUuid");
            if (!string.IsNullOrEmpty(serverUuid))
            {
                ServerGameUuid = serverUuid;
                Console.WriteLine($"[API] ✅ 服务端 gameUuid: {ServerGameUuid}");
            }

            var isLeague = json.Contains("\"isLeague\"") && json.Contains("true");
            if (isLeague)
            {
                Console.WriteLine("[API] 联赛对局已匹配");
            }
            else
            {
                Console.WriteLine("[API] 非联赛对局，但验证码已获取");
            }

            LastLeagueResult = isLeague;
            return isLeague;
        }
        catch (Exception e)
        {
            LastError = $"check-league 异常: {e.Message}";
            Console.WriteLine($"[API] ⚠️ {LastError}");
            throw; // 向上抛出，让重试逻辑区分"网络错误"和"非联赛"
        }
    }

    /// <summary>
    /// 启动时调用，上报评分获取验证码（无需进入对局）
    /// </summary>
    public static async Task<bool> UploadRatingAsync(
        string playerId,
        ulong accountIdLo,
        int rating = 0,
        string region = "CN",
        string mode = "solo")
    {
        LastError = "";

        var body = new Dictionary<string, object>
        {
            ["playerId"] = playerId,
            ["accountIdLo"] = accountIdLo.ToString(),
            ["rating"] = rating,
            ["mode"] = mode,
            ["region"] = region
        };

        try
        {
            var (ok, json) = await PostAsync("/api/plugin/upload-rating", body);
            if (!ok) return false;

            var vc = ExtractJsonString(json, "verificationCode");
            if (!string.IsNullOrEmpty(vc))
            {
                VerificationCode = vc;
                Console.WriteLine("[API] ✅ 验证码: " + VerificationCode);
                return true;
            }
            else
            {
                Console.WriteLine("[API] ⚠️ upload-rating 响应中无 verificationCode，json=" + json);
            }
        }
        catch (Exception e)
        {
            LastError = "upload-rating 异常: " + e.Message;
            Console.WriteLine("[API] ⚠️ " + LastError);
        }
        return false;
    }

    /// <summary>
    /// 游戏结束时调用，上报排名
    /// </summary>
    public static async Task<bool> UpdatePlacementAsync(
        string gameUuid,
        string playerId,
        ulong accountIdLo,
        int placement,
        List<string>? reconnectTimes = null)
    {
        LastError = "";

        var body = new Dictionary<string, object>
        {
            ["gameUuid"] = gameUuid,
            ["accountIdLo"] = accountIdLo.ToString(),
            ["placement"] = placement,
            ["playerId"] = playerId
        };
        if (reconnectTimes != null && reconnectTimes.Count > 0)
            body["reconnectTimes"] = reconnectTimes;

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

                var finalized = ExtractJsonBool(json, "finalized");
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
        if (!string.IsNullOrEmpty(ApiKey))
            request.Headers.Add("Authorization", $"Bearer {ApiKey}");

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

    private static bool ExtractJsonBool(string json, string key)
    {
        var search = $"\"{key}\"";
        var idx = json.IndexOf(search);
        if (idx < 0) return false;
        idx += search.Length;
        // 跳过 ": "
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':' || json[idx] == '\t'))
            idx++;
        return idx < json.Length && json.Substring(idx).StartsWith("true");
    }
}
}
