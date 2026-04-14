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
        private const string ApiBaseUrl = "https://da.iceshoes.dpdns.org/";
        private const string PluginHeaderName = "X-HDT-Plugin";
        private static readonly string PluginHeaderValue = typeof(RatingTracker).Assembly.GetName().Version.ToString(3);

        // ── 状态 ──────────────────────────────────────────
        private bool _enabled;
        private bool _wasInBgGame;
        private DateTime _gameEndTime = DateTime.MinValue;
        private string _cachedPlayerId;
        private string _cachedAccountIdLo;
        private DateTime _bgGameStartTime = DateTime.MinValue;
        private static readonly TimeSpan IdReadDelay = TimeSpan.FromSeconds(3);
        private bool _heroLogged;
        private bool _lobbyLogged;
        private bool _isLeagueGame;
        private string _cachedGameUuid;
        private int _lastStepValue = -1;
        private int _placementRetryCount;
        private const int MaxPlacementRetries = 10;

        // ── HTTP + JSON ───────────────────────────────────
        private HttpClient _httpClient;
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

                _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                _httpClient.DefaultRequestHeaders.Add(PluginHeaderName, PluginHeaderValue);

                Log("插件已启动（纯联赛模式）");
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

                    if (_bgGameStartTime == DateTime.MinValue)
                        _bgGameStartTime = DateTime.Now;

                    // STEP 检测：STEP 13 (MAIN_CLEANUP) = 第一轮战斗结束
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
                        }
                        if (!string.IsNullOrEmpty(_cachedPlayerId) && _cachedPlayerId != "unknown")
                        {
                            if (string.IsNullOrEmpty(_cachedAccountIdLo))
                            {
                                _cachedAccountIdLo = GetAccountIdLo();
                            }
                            if (string.IsNullOrEmpty(_cachedGameUuid))
                            {
                                _cachedGameUuid = GetGameUuid();
                            }
                        }
                    }

                    // lobby 玩家名单（不带英雄）
                    if (!string.IsNullOrEmpty(_cachedPlayerId) && _cachedPlayerId != "unknown" && !_lobbyLogged)
                    {
                        LogLobbyPlayers(includeHeroes: false);
                        _lobbyLogged = true;
                    }
                }
                else if (_wasInBgGame && Core.Game.IsInMenu)
                {
                    if (_gameEndTime == DateTime.MinValue)
                    {
                        _gameEndTime = DateTime.Now;
                        _placementRetryCount = 0;
                        return;
                    }

                    if ((DateTime.Now - _gameEndTime).TotalSeconds < 2)
                        return;

                    // 游戏结束处理
                    if (_isLeagueGame)
                    {
                        bool placementUploaded = UpdateLeaguePlacement(_cachedAccountIdLo, _cachedGameUuid);
                        if (!placementUploaded && _placementRetryCount < MaxPlacementRetries)
                        {
                            _placementRetryCount++;
                            Log($"OnUpdate: placement 尚未就绪，第 {_placementRetryCount}/{MaxPlacementRetries} 次重试");
                            return; // 下个 OnUpdate 周期再试
                        }
                        if (!placementUploaded)
                            Log("OnUpdate: placement 重试耗尽，放弃本次上传");
                    }

                    // 重置状态
                    _wasInBgGame = false;
                    _gameEndTime = DateTime.MinValue;
                    _cachedPlayerId = null;
                    _cachedAccountIdLo = null;
                    _cachedGameUuid = null;
                    _lastStepValue = -1;
                    _bgGameStartTime = DateTime.MinValue;
                    _heroLogged = false;
                    _lobbyLogged = false;
                    _isLeagueGame = false;
                    _placementRetryCount = 0;
                }
            }
            catch (Exception ex)
            {
                Log("OnUpdate 异常: " + ex.Message);
            }
        }

        // ── HTTP 通用方法 ─────────────────────────────────

        private (bool ok, string body) PostJson(string endpoint, string jsonBody)
        {
            try
            {
                var result = PostJsonOnce(endpoint, jsonBody);
                if (!result.ok)
                    Log($"POST {endpoint} 失败: HTTP {result.statusCode}");
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

            var response = _httpClient.SendAsync(request).Result;
            string body = response.Content.ReadAsStringAsync().Result;

            if (response.IsSuccessStatusCode)
                return (true, (int)response.StatusCode, body);

            Log($"POST {endpoint} 失败: {(int)response.StatusCode} {body}");
            return (false, (int)response.StatusCode, body);
        }

        // ── 联赛对局 ────────────────────────────────────────

        /// <summary>
        /// STEP 13 时检查等待队列，同时返回验证码
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
                        ["accountIdLo"] = _cachedAccountIdLo ?? "",
                        ["gameUuid"] = gameUuid,
                        ["accountIdLoList"] = accountIdList,
                        ["players"] = playerDetails,
                        ["mode"] = mode,
                        ["region"] = region,
                        ["startedAt"] = startedAt,
                    });

                    var (ok, body) = PostJson("/api/plugin/check-league", json);

                    // 网络失败时重试（数据已就绪，只是请求没送到）
                    if (!ok)
                    {
                        for (int retry = 1; retry <= 3 && !ok; retry++)
                        {
                            Log($"CheckLeagueQueue: 第{retry}次重试...");
                            System.Threading.Thread.Sleep(1000 * retry);
                            (ok, body) = PostJson("/api/plugin/check-league", json);
                        }
                    }

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
                            // >>> BEGIN TEST_MODE
                            else
                            {
                                _isLeagueGame = false;
                                Log("CheckLeagueQueue: 未匹配到等待组，普通天梯局");
                            }
                            // <<< END TEST_MODE

                            // 验证码（首次上传时服务端生成）
                            if (dict != null && dict.ContainsKey("verificationCode"))
                                Log($"验证码: {dict["verificationCode"]} (前往联赛网站注册时使用)");
                        }
                        catch (Exception ex)
                        {
                            Log($"CheckLeagueQueue: 解析响应异常: {ex.Message}, body={body}");
                        }
                    }
                    else
                    {
                        Log($"CheckLeagueQueue: 请求失败，body={body}");
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
        /// 返回 true 表示已成功上传（或已确认上传），false 表示 placement 尚未就绪需重试
        /// </summary>
        private bool UpdateLeaguePlacement(string accountIdLo, string gameUuid)
        {
            if (_httpClient == null) return true;
            if (string.IsNullOrEmpty(gameUuid) || string.IsNullOrEmpty(accountIdLo)) return true;

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
                Log("UpdateLeaguePlacement: placement 为 null，需要重试");
                return false;
            }

            Task.Run(() =>
            {
                try
                {
                    string json = _json.Serialize(new Dictionary<string, object>
                    {
                        ["playerId"] = _cachedPlayerId ?? "",
                        ["gameUuid"] = gameUuid,
                        ["accountIdLo"] = accountIdLo,
                        ["placement"] = placement.Value,
                    });

                    var (ok, body) = PostJson("/api/plugin/update-placement", json);

                    // HTTP 失败时重试（排名数据已就绪，只需重发请求）
                    if (!ok)
                    {
                        for (int retry = 1; retry <= 3 && !ok; retry++)
                        {
                            Log($"UpdateLeaguePlacement: 第{retry}次重试...");
                            System.Threading.Thread.Sleep(1000 * retry);
                            (ok, body) = PostJson("/api/plugin/update-placement", json);
                        }
                    }

                    if (ok)
                    {
                        int points = placement.Value == 1 ? 9 : Math.Max(1, 9 - placement.Value);
                        Log($"UpdateLeaguePlacement: gameUuid={gameUuid} 排名={placement.Value} 积分={points}");

                        try
                        {
                            var dict = _json.Deserialize<Dictionary<string, object>>(body);
                            if (dict != null && dict.ContainsKey("finalized") && (bool)dict["finalized"])
                                Log($"对局已全部提交，endedAt 已写入");
                        }
                        catch { }
                    }
                    else
                    {
                        Log($"UpdateLeaguePlacement: 请求失败，排名可能丢失 gameUuid={gameUuid} placement={placement.Value}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"UpdateLeaguePlacement 异常: {ex.Message}");
                }
            });

            return true; // placement 有值，后续上传由 Task.Run 异步完成
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
                            return p.AccountId.Lo.ToString();
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

                if (!includeHeroes)
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
                    var (ok, body) = PostJson("/api/plugin/check-league", "{}");
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
