using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Hearthstone_Deck_Tracker.API;
using HearthDb;

namespace HDT_BGTracker
{
    public class RatingTracker
    {
        // ── 配置 ──────────────────────────────────────────
        private const string ApiBaseUrl = "https://你的域名";  // 生产环境改成实际域名
        private const string PluginHeaderName = "X-HDT-Plugin";
        private const string PluginHeaderValue = "v1";
        private static string TokenFile =>
            Path.Combine(LogDir, ".plugin_token");

        // ── 状态 ──────────────────────────────────────────
        private bool _enabled;
        private bool _wasInBgGame;
        private DateTime _gameEndTime = DateTime.MinValue;
        private bool _ratingUploaded;
        private string _cachedPlayerId;
        private string _cachedAccountIdLo;
        private DateTime _bgGameStartTime = DateTime.MinValue;
        private static readonly TimeSpan IdReadDelay = TimeSpan.FromSeconds(3);
        private bool _heroLogged;
        private bool _lobbyLogged;
        private bool _leagueMatchCreated;
        private bool _isLeagueGame;
        private string _cachedGameUuid;
        private int _lastStepValue = -1;

        // ── HTTP + JSON ───────────────────────────────────
        private HttpClient _httpClient;
        private string _authToken;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        // ── 日志 ──────────────────────────────────────────
        private static string LogDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "HearthstoneDeckTracker", "BGTracker");
        private static string LogFile => Path.Combine(LogDir, "tracker.log");

        public void Start()
        {
            if (_enabled) return;
            _enabled = true;

            try
            {
                Directory.CreateDirectory(LogDir);
                CleanOldLogs();

                // 初始化 HttpClient
                _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                _httpClient.DefaultRequestHeaders.Add(PluginHeaderName, PluginHeaderValue);

                // 加载本地保存的 token
                if (File.Exists(TokenFile))
                {
                    _authToken = File.ReadAllText(TokenFile).Trim();
                    if (!string.IsNullOrEmpty(_authToken))
                        Log("已加载本地 token");
                }

                Log("插件已启动（HTTP API 模式）");
            }
            catch (Exception ex)
            {
                Log("插件启动异常: " + ex.Message);
            }
        }

        public void Stop()
        {
            _enabled = false;
            _httpClient?.Dispose();
            _httpClient = null;
            Log("插件已停止");
        }

        /// <summary>
        /// 由 OnUpdate() 每 ~100ms 调用一次
        /// </summary>
        public void OnUpdate()
        {
            if (!_enabled) return;

            try
            {
                var game = Core.Game;
                bool inBgGame = game.IsBattlegroundsMatch && !game.IsInMenu;

                if (inBgGame)
                {
                    _wasInBgGame = true;
                    _ratingUploaded = false;

                    if (_bgGameStartTime == DateTime.MinValue)
                        _bgGameStartTime = DateTime.Now;

                    // STEP 13 检测
                    try
                    {
                        var gameEntity = Core.Game?.Entities?.Values
                            ?.FirstOrDefault(e => e.Name == "GameEntity");
                        if (gameEntity != null)
                        {
                            int currentStep = gameEntity.GetTag(HearthDb.Enums.GameTag.STEP);
                            if (currentStep != _lastStepValue)
                            {
                                _lastStepValue = currentStep;
                                if (currentStep == 13 && !_heroLogged)
                                {
                                    LogLobbyPlayers(includeHeroes: true);
                                    _heroLogged = true;
                                    CheckLeagueQueue();
                                }
                            }
                        }
                    }
                    catch { }

                    // 延迟 3 秒后读取 PlayerId
                    if (DateTime.Now - _bgGameStartTime >= IdReadDelay)
                    {
                        if (string.IsNullOrEmpty(_cachedPlayerId))
                        {
                            _cachedPlayerId = GetPlayerId();
                            Log($"缓存: playerId={_cachedPlayerId}");
                        }
                        if (!string.IsNullOrEmpty(_cachedPlayerId) && _cachedPlayerId != "unknown")
                        {
                            if (string.IsNullOrEmpty(_cachedAccountIdLo))
                            {
                                _cachedAccountIdLo = GetAccountIdLo();
                                if (!string.IsNullOrEmpty(_cachedAccountIdLo))
                                    Log($"缓存: accountIdLo={_cachedAccountIdLo}");
                            }
                            if (string.IsNullOrEmpty(_cachedGameUuid))
                            {
                                _cachedGameUuid = GetGameUuid();
                                if (!string.IsNullOrEmpty(_cachedGameUuid))
                                    Log($"缓存: gameUuid={_cachedGameUuid}");
                            }
                        }
                    }

                    // lobby 玩家名单（不带英雄）
                    if (!string.IsNullOrEmpty(_cachedPlayerId) && _cachedPlayerId != "unknown" && !_lobbyLogged)
                    {
                        LogLobbyPlayers(includeHeroes: false);
                        _lobbyLogged = true;
                    }

                    // 创建联赛对局文档
                    if (_heroLogged && !_leagueMatchCreated && _isLeagueGame
                        && !string.IsNullOrEmpty(_cachedPlayerId) && _cachedPlayerId != "unknown")
                    {
                        // HTTP 模式下联赛文档由 check-league 端点创建，这里只需标记
                        _leagueMatchCreated = true;
                    }
                }
                else if (_wasInBgGame && Core.Game.IsInMenu && !_ratingUploaded)
                {
                    if (_gameEndTime == DateTime.MinValue)
                    {
                        _gameEndTime = DateTime.Now;
                        return;
                    }

                    if ((DateTime.Now - _gameEndTime).TotalSeconds < 2)
                        return;

                    string cachedPlayerId = _cachedPlayerId;
                    string cachedAccountIdLo = _cachedAccountIdLo;
                    string cachedGameUuid = _cachedGameUuid;

                    if (_isLeagueGame)
                    {
                        IncrementLeagueCount(cachedPlayerId);
                        UpdateLeaguePlacement(cachedAccountIdLo, cachedGameUuid);
                    }
                    else
                    {
                        TryUploadRating();
                    }

                    _wasInBgGame = false;
                    _gameEndTime = DateTime.MinValue;
                    _cachedPlayerId = null;
                    _cachedAccountIdLo = null;
                    _cachedGameUuid = null;
                    _lastStepValue = -1;
                    _bgGameStartTime = DateTime.MinValue;
                    _heroLogged = false;
                    _lobbyLogged = false;
                    _leagueMatchCreated = false;
                    _isLeagueGame = false;
                }
            }
            catch (Exception ex)
            {
                Log("OnUpdate 异常: " + ex.Message);
            }
        }

        // ── HTTP 通用方法 ─────────────────────────────────

        /// <summary>
        /// POST JSON，自动带 auth token + X-HDT-Plugin header。
        /// 如果服务器返回 401，清除本地 token 并重试一次（获取新 token）。
        /// 返回 (success, responseBody)。
        /// </summary>
        private (bool ok, string body) PostJson(string endpoint, string jsonBody)
        {
            try
            {
                var result = PostJsonOnce(endpoint, jsonBody);
                if (result.statusCode == 401 && !string.IsNullOrEmpty(_authToken))
                {
                    // token 过期，清除并重试
                    Log("token 已过期，重新获取");
                    _authToken = null;
                    if (File.Exists(TokenFile))
                        try { File.Delete(TokenFile); } catch { }
                    result = PostJsonOnce(endpoint, jsonBody);
                }
                return result.ok ? (true, result.body) : (false, result.body);
            }
            catch (Exception ex)
            {
                Log($"POST {endpoint} 异常: {ex.Message}");
                return (false, null);
            }
        }

        private (bool ok, int statusCode, string body) PostJsonOnce(string endpoint, string jsonBody)
        {
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}{endpoint}")
            {
                Content = content
            };
            if (!string.IsNullOrEmpty(_authToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);

            var response = _httpClient.SendAsync(request).Result;
            string body = response.Content.ReadAsStringAsync().Result;

            if (response.IsSuccessStatusCode)
                return (true, (int)response.StatusCode, body);

            Log($"POST {endpoint} 失败: {(int)response.StatusCode} {body}");
            return (false, (int)response.StatusCode, body);
        }

        /// <summary>
        /// 保存 token 到本地文件
        /// </summary>
        private void SaveToken(string token)
        {
            _authToken = token;
            try
            {
                File.WriteAllText(TokenFile, token);
            }
            catch (Exception ex)
            {
                Log($"保存 token 失败: {ex.Message}");
            }
        }

        // ── 业务逻辑 ──────────────────────────────────────

        /// <summary>
        /// 联赛对局结束：通过 upload-rating 让 server 端 leagueCount +1
        /// </summary>
        private void IncrementLeagueCount(string playerId)
        {
            if (_httpClient == null) return;
            if (string.IsNullOrEmpty(playerId) || playerId == "unknown") return;

            Task.Run(() =>
            {
                try
                {
                    string region = GetRegion();
                    string json = _json.Serialize(new Dictionary<string, object>
                    {
                        ["playerId"] = playerId,
                        ["accountIdLo"] = _cachedAccountIdLo ?? "",
                        ["rating"] = Core.Game.CurrentBattlegroundsRating ?? 0,
                        ["mode"] = Core.Game.IsBattlegroundsDuosMatch ? "duo" : "solo",
                        ["gameUuid"] = _cachedGameUuid ?? "",
                        ["region"] = region,
                    });

                    var (ok, body) = PostJson("/api/plugin/upload-rating", json);
                    if (ok)
                    {
                        Log($"IncrementLeagueCount: playerId={playerId} 已处理");

                        // 保存 token（首次上传时 server 返回）
                        try
                        {
                            var dict = _json.Deserialize<Dictionary<string, object>>(body);
                            if (dict != null && dict.ContainsKey("token"))
                                SaveToken(dict["token"].ToString());
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"IncrementLeagueCount 异常: {ex.Message}");
                }
            });
        }

        private void TryUploadRating()
        {
            try
            {
                if (string.IsNullOrEmpty(_cachedPlayerId) || _cachedPlayerId == "unknown")
                {
                    _cachedPlayerId = GetPlayerId();
                    if (string.IsNullOrEmpty(_cachedPlayerId) || _cachedPlayerId == "unknown")
                    {
                        _cachedPlayerId = "unknown";
                        Log("警告: 无法获取 PlayerId，使用 'unknown'");
                    }
                }

                // 用反射刷新分数缓存
                var gameType = Core.Game.GetType();
                var cacheMethod = gameType.GetMethod("CacheBattlegroundsRatingInfo",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                cacheMethod?.Invoke(Core.Game, null);

                var rating = Core.Game.CurrentBattlegroundsRating;
                if (rating.HasValue)
                {
                    string mode = Core.Game.IsBattlegroundsDuosMatch ? "duo" : "solo";

                    int? placement = null;
                    try
                    {
                        var stats = Core.Game.CurrentGameStats;
                        var details = stats?.BattlegroundsDetails;
                        placement = details?.FinalPlacement;
                        Log($"排名读取: FinalPlacement = {placement?.ToString() ?? "null"}");
                    }
                    catch (Exception ex)
                    {
                        Log($"排名读取异常: {ex.Message}");
                    }

                    string gameUuid = _cachedGameUuid ?? "";
                    UploadRating(rating.Value, mode, gameUuid);
                }
                else
                {
                    Log("无法读取分数（CurrentBattlegroundsRating 为 null）");
                    _ratingUploaded = true;
                }
            }
            catch (Exception ex)
            {
                Log("读取分数异常: " + ex.Message);
            }
        }

        private void UploadRating(int rating, string mode, string gameUuid)
        {
            if (_httpClient == null)
            {
                Log("HttpClient 未初始化，跳过上传");
                _ratingUploaded = true;
                return;
            }

            string playerId = string.IsNullOrEmpty(_cachedPlayerId) ? "unknown" : _cachedPlayerId;
            string accountIdLo = _cachedAccountIdLo;
            string region = GetRegion();

            Task.Run(() =>
            {
                try
                {
                    string json = _json.Serialize(new Dictionary<string, object>
                    {
                        ["playerId"] = playerId,
                        ["accountIdLo"] = accountIdLo ?? "",
                        ["rating"] = rating,
                        ["mode"] = mode,
                        ["gameUuid"] = gameUuid,
                        ["region"] = region,
                    });

                    var (ok, body) = PostJson("/api/plugin/upload-rating", json);
                    if (ok)
                    {
                        _ratingUploaded = true;
                        Log($"已上传分数: {rating} ({mode}) playerId={playerId} gameUuid={gameUuid}");

                        try
                        {
                            var dict = _json.Deserialize<Dictionary<string, object>>(body);
                            if (dict != null)
                            {
                                if (dict.ContainsKey("verificationCode"))
                                    Log($"联赛验证码: {dict["verificationCode"]} (前往联赛网站注册时使用)");
                                if (dict.ContainsKey("token"))
                                    SaveToken(dict["token"].ToString());
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log("上传失败: " + ex.Message);
                }
            });
        }

        // ── 联赛对局 ────────────────────────────────────────

        /// <summary>
        /// STEP 13 时检查等待队列
        /// </summary>
        private void CheckLeagueQueue()
        {
            if (_httpClient == null) return;

            Task.Run(() =>
            {
                try
                {
                    var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                    if (lobbyInfo?.Players == null || lobbyInfo.Players.Count == 0)
                    {
                        Log("CheckLeagueQueue: LobbyInfo 未就绪，跳过");
                        return;
                    }

                    var accountIdList = new List<string>();
                    var playerDetails = new Dictionary<string, object>();

                    foreach (var p in lobbyInfo.Players)
                    {
                        if (p.AccountId == null) continue;
                        string lo = p.AccountId.Lo.ToString();
                        accountIdList.Add(lo);
                        playerDetails[lo] = new Dictionary<string, object>
                        {
                            ["battleTag"] = p.Name ?? "",
                            ["displayName"] = p.Name ?? "",
                            ["heroCardId"] = p.HeroCardId ?? "",
                            ["heroName"] = GetHeroName(p.HeroCardId ?? ""),
                        };
                    }

                    if (accountIdList.Count == 0)
                    {
                        Log("CheckLeagueQueue: 无有效 accountIdLo，跳过");
                        return;
                    }

                    Log($"CheckLeagueQueue: 本局玩家 accountIdLo = [{string.Join(", ", accountIdList)}]");

                    string gameUuid = lobbyInfo.GameUuid ?? _cachedGameUuid ?? "";
                    string mode = Core.Game.IsBattlegroundsDuosMatch ? "duo" : "solo";
                    string region = GetRegion();
                    string startedAt = _bgGameStartTime != DateTime.MinValue
                        ? _bgGameStartTime.ToUniversalTime().ToString("o")
                        : DateTime.UtcNow.ToString("o");

                    string json = _json.Serialize(new Dictionary<string, object>
                    {
                        ["playerId"] = _cachedPlayerId ?? "",
                        ["gameUuid"] = gameUuid,
                        ["accountIdLoList"] = accountIdList,
                        ["players"] = playerDetails,
                        ["mode"] = mode,
                        ["region"] = region,
                        ["startedAt"] = startedAt,
                    });

                    var (ok, body) = PostJson("/api/plugin/check-league", json);
                    if (ok)
                    {
                        try
                        {
                            var dict = _json.Deserialize<Dictionary<string, object>>(body);
                            bool isLeague = dict != null && dict.ContainsKey("isLeague") && (bool)dict["isLeague"];
                            if (isLeague)
                            {
                                _isLeagueGame = true;
                                Log("CheckLeagueQueue: ★ 联赛对局确认！");
                            }
                            else
                            {
                                // [TESTING] 暂时跳过联赛判断，所有对局都当联赛处理
                                // _isLeagueGame = false;
                                // Log("CheckLeagueQueue: 未匹配到等待组，普通天梯局");
                                _isLeagueGame = true;
                                Log("CheckLeagueQueue: [TESTING] 跳过等待组匹配，强制标记为联赛对局");
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"CheckLeagueQueue 异常: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 游戏结束时，更新自己在联赛对局中的排名
        /// </summary>
        private void UpdateLeaguePlacement(string accountIdLo, string gameUuid)
        {
            if (_httpClient == null) return;
            if (string.IsNullOrEmpty(gameUuid) || string.IsNullOrEmpty(accountIdLo)) return;

            Task.Run(() =>
            {
                try
                {
                    int? placement = null;
                    try
                    {
                        var stats = Core.Game.CurrentGameStats;
                        var details = stats?.BattlegroundsDetails;
                        placement = details?.FinalPlacement;
                    }
                    catch (Exception ex)
                    {
                        Log($"UpdateLeaguePlacement: 读取 placement 异常: {ex.Message}");
                    }

                    if (!placement.HasValue)
                    {
                        Log("UpdateLeaguePlacement: placement 为 null，跳过更新");
                        return;
                    }

                    string json = _json.Serialize(new Dictionary<string, object>
                    {
                        ["playerId"] = _cachedPlayerId ?? "",
                        ["gameUuid"] = gameUuid,
                        ["accountIdLo"] = accountIdLo,
                        ["placement"] = placement.Value,
                    });

                    var (ok, body) = PostJson("/api/plugin/update-placement", json);
                    if (ok)
                    {
                        int points = placement.Value == 1 ? 9 : Math.Max(1, 9 - placement.Value);
                        Log($"UpdateLeaguePlacement: gameUuid={gameUuid} 排名={placement.Value} 积分={points}");

                        try
                        {
                            var dict = _json.Deserialize<Dictionary<string, object>>(body);
                            if (dict != null && dict.ContainsKey("finalized") && (bool)dict["finalized"])
                                Log($"UpdateLeagueMatch: 对局已全部提交，endedAt 已写入");
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"UpdateLeaguePlacement 异常: {ex.Message}");
                }
            });
        }

        // ── 辅助方法 ──────────────────────────────────────

        private string GetPlayerId()
        {
            try
            {
                var player = Core.Game?.Player;
                if (player != null)
                {
                    string name = player.Name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        Log($"GetPlayerId: Player.Name = {name}");
                        return name;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetPlayerId: Player.Name 读取失败: {ex.Message}");
            }

            try
            {
                var entities = Core.Game?.Player?.PlayerEntities?.ToList();
                if (entities != null)
                {
                    foreach (var entity in entities)
                    {
                        string name = entity.Name;
                        if (!string.IsNullOrEmpty(name))
                        {
                            Log($"GetPlayerId: PlayerEntity.Name = {name}");
                            return name;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetPlayerId: PlayerEntities 读取失败: {ex.Message}");
            }

            Log("GetPlayerId: 未找到有效 ID");
            return "unknown";
        }

        private string GetAccountIdLo()
        {
            try
            {
                var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                if (lobbyInfo?.Players != null && !string.IsNullOrEmpty(_cachedPlayerId))
                {
                    string myNameNoTag = _cachedPlayerId;
                    int hashIdx = myNameNoTag.IndexOf('#');
                    if (hashIdx > 0) myNameNoTag = myNameNoTag.Substring(0, hashIdx);

                    foreach (var p in lobbyInfo.Players)
                    {
                        if (p.Name == myNameNoTag && p.AccountId != null)
                        {
                            string lo = p.AccountId.Lo.ToString();
                            Log($"GetAccountIdLo: 自己 = {p.Name}, Lo = {lo}");
                            return lo;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetAccountIdLo 异常: {ex.Message}");
            }
            return null;
        }

        private string GetGameUuid()
        {
            try
            {
                var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                if (lobbyInfo != null)
                {
                    string uuid = lobbyInfo.GameUuid ?? "";
                    Log($"GetGameUuid: {uuid}");
                    return uuid;
                }
            }
            catch (Exception ex)
            {
                Log($"GetGameUuid 异常: {ex.Message}");
            }
            return "";
        }

        private void LogLobbyPlayers(bool includeHeroes = false)
        {
            try
            {
                var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                if (lobbyInfo == null) return;

                var players = lobbyInfo.Players;
                if (players == null || players.Count == 0) return;

                string gameUuid = lobbyInfo.GameUuid ?? "";
                string phase = includeHeroes ? "英雄选择后" : "游戏开始";
                Log($"=== Lobby {phase} (GameUuid: {gameUuid}) ===");

                string logText = "";
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    string name = p.Name;
                    string acctLo = p.AccountId?.Lo.ToString() ?? "?";

                    if (includeHeroes)
                    {
                        string heroId = p.HeroCardId ?? "";
                        string heroName = GetHeroName(heroId);
                        logText += $"\n  [{i}] {name} (Lo={acctLo}) 英雄={heroName}";
                    }
                    else
                    {
                        logText += $"\n  [{i}] {name} (Lo={acctLo})";
                    }
                }
                Log(logText);
                Log($"共 {players.Count} 个玩家");
            }
            catch (Exception ex)
            {
                string phase = includeHeroes ? "英雄选择后" : "游戏开始";
                Log($"LogLobbyPlayers({phase}) 等待中: {ex.Message}");
            }
        }

        private static string GetHeroName(string heroCardId)
        {
            if (string.IsNullOrEmpty(heroCardId)) return "";
            try
            {
                if (Cards.All.TryGetValue(heroCardId, out var card))
                {
                    return card.GetLocName(HearthDb.Enums.Locale.zhCN) ?? card.Name ?? heroCardId;
                }
            }
            catch (Exception ex)
            {
                Log($"GetHeroName 异常: {ex.Message}");
            }
            return heroCardId;
        }

        public static void TestHearthDb()
        {
            try
            {
                Log("=== HearthDb 验证开始 ===");
                Log($"Cards.All 总数: {Cards.All.Count}");

                string[] testHeroes = {
                    "TB_BaconShop_HERO_56",
                    "TB_BaconShop_HERO_50",
                    "BG20_HERO_202",
                    "BG31_HERO_802",
                };

                foreach (var heroId in testHeroes)
                {
                    if (Cards.All.TryGetValue(heroId, out var card))
                    {
                        string enName = card.Name ?? "?";
                        string zhName = card.GetLocName(HearthDb.Enums.Locale.zhCN) ?? "?";
                        Log($"  {heroId} → EN: {enName}, CN: {zhName}");
                    }
                    else
                    {
                        Log($"  {heroId} → NOT FOUND");
                    }
                }

                Log("=== HearthDb 验证完成 ===");
            }
            catch (Exception ex)
            {
                Log($"HearthDb 验证失败: {ex.Message}");
            }
        }

        private string GetRegion()
        {
            try
            {
                return Core.Game.CurrentRegion.ToString();
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        /// <summary>
        /// 测试 API 连接
        /// </summary>
        public void TestConnection()
        {
            if (_httpClient == null)
            {
                Log("HttpClient 未初始化");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    string json = _json.Serialize(new Dictionary<string, object>
                    {
                        ["playerId"] = "__test__",
                        ["accountIdLo"] = "",
                        ["rating"] = -1,
                        ["mode"] = "test",
                        ["gameUuid"] = "",
                        ["region"] = "TEST",
                    });

                    var (ok, body) = PostJson("/api/plugin/upload-rating", json);
                    if (ok)
                        Log("API 连接测试成功 ✓");
                    else
                        Log("API 连接测试失败");
                }
                catch (Exception ex)
                {
                    Log("API 连接测试失败: " + ex.Message);
                }
            });
        }

        // 验证码现在由服务端生成，客户端不再需要
        // public static string GenerateVerificationCode(...) { ... }

        private static void Log(string msg)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch { }
        }

        private static void CleanOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogDir)) return;
                var cutoff = DateTime.Now.AddDays(-3);
                var logFiles = Directory.GetFiles(LogDir, "*.log");
                foreach (var file in logFiles)
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
            }
            catch { }
        }
    }
}
