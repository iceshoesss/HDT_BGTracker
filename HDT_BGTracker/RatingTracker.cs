using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker.API;
using HearthDb;
using MongoDB.Driver;

namespace HDT_BGTracker
{
    public class RatingTracker
    {
        // ── 配置 ──────────────────────────────────────────
        private const string MongoUrl = "mongodb://YOUR_MONGO_HOST:27017";
        private const string DbName = "hearthstone";
        private const string CollectionName = "bg_ratings";
        private const string LeagueCollectionName = "league_matches";

        // ── 状态 ──────────────────────────────────────────
        private bool _enabled;
        private bool _wasInBgGame;
        private DateTime _gameEndTime = DateTime.MinValue;
        private bool _ratingUploaded;
        private string _cachedPlayerId;
        private string _cachedAccountIdLo; // 玩家自己的 AccountId.Lo（字符串避免大数问题）
        private DateTime _bgGameStartTime = DateTime.MinValue;
        private static readonly TimeSpan IdReadDelay = TimeSpan.FromSeconds(3);
        private bool _heroLogged; // 英雄名是否已输出
        private bool _lobbyLogged; // lobby 玩家名单是否已输出（不带英雄）
        private bool _leagueMatchCreated; // 联赛文档是否已创建
        private string _cachedGameUuid; // 当局游戏 UUID
        private int _lastStepValue = -1; // 上一次 STEP tag 的值，用于检测变化
        // private LobbyOverlay _overlay; // 浮动窗口已禁用

        private MongoDB.Driver.MongoClient _mongoClient;
        private MongoDB.Driver.IMongoCollection<MongoDB.Bson.BsonDocument> _collection;
        private MongoDB.Driver.IMongoCollection<MongoDB.Bson.BsonDocument> _leagueCollection;

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
                _mongoClient = new MongoDB.Driver.MongoClient(MongoUrl);
                var db = _mongoClient.GetDatabase(DbName);
                _collection = db.GetCollection<MongoDB.Bson.BsonDocument>(CollectionName);
                _leagueCollection = db.GetCollection<MongoDB.Bson.BsonDocument>(LeagueCollectionName);
                Log("插件已启动，MongoDB 已连接");
            }
            catch (Exception ex)
            {
                Log("MongoDB 连接失败: " + ex.Message);
            }
        }

        public void Stop()
        {
            _enabled = false;
            _mongoClient = null;
            _collection = null;
            _leagueCollection = null;
            // _overlay = null; // 浮动窗口已禁用
            Log("插件已停止");
        }

        // public void SetOverlay(LobbyOverlay overlay) // 浮动窗口已禁用
        // {
        //     _overlay = overlay;
        // }

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

                    // 记录游戏开始时间
                    if (_bgGameStartTime == DateTime.MinValue)
                    {
                        _bgGameStartTime = DateTime.Now;
                    }

                    // STEP 13 (MAIN_CLEANUP) = 第一轮战斗结束，英雄选择早已完成
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
                                }
                            }
                        }
                    }
                    catch { }

                    // 延迟 3 秒后再读取 PlayerId（游戏初始化需要时间）
                    if (DateTime.Now - _bgGameStartTime >= IdReadDelay)
                    {
                        if (string.IsNullOrEmpty(_cachedPlayerId))
                        {
                            _cachedPlayerId = GetPlayerId();
                            Log($"缓存: playerId={_cachedPlayerId}");
                        }
                        // PlayerId 获取后，再尝试获取 accountIdLo 和 gameUuid（LobbyInfo 可能比 PlayerId 更晚就绪）
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

                    // PlayerId 获取后，输出 lobby 玩家名单（不带英雄，此时英雄还没选）
                    if (!string.IsNullOrEmpty(_cachedPlayerId) && _cachedPlayerId != "unknown" && !_lobbyLogged)
                    {
                        LogLobbyPlayers(includeHeroes: false);
                        _lobbyLogged = true;
                    }

                    // STEP 13 + PlayerId 都就绪后，创建联赛对局文档
                    if (_heroLogged && !_leagueMatchCreated
                        && !string.IsNullOrEmpty(_cachedPlayerId) && _cachedPlayerId != "unknown")
                    {
                        CreateLeagueMatch();
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

                    // 等待 2 秒让分数数据刷新
                    if ((DateTime.Now - _gameEndTime).TotalSeconds < 2)
                        return;

                    // 捕获缓存值，避免 Task.Run 并发时被主线程 cleanup 竞争覆盖
                    string cachedPlayerId = _cachedPlayerId;
                    string cachedAccountIdLo = _cachedAccountIdLo;
                    string cachedGameUuid = _cachedGameUuid;

                    TryUploadRating();
                    UpdateLeaguePlacement(cachedAccountIdLo, cachedGameUuid);

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
                    // _overlay?.Hide(); // 浮动窗口已禁用
                }
            }
            catch (Exception ex)
            {
                Log("OnUpdate 异常: " + ex.Message);
            }
        }

        private void TryUploadRating()
        {
            try
            {
                // 兜底：确保 playerId 不为 null
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

                    // 尝试获取排名
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

                    // 收集对手信息
                    var opponents = GetOpponentInfo();
                    string gameUuid = _cachedGameUuid ?? "";
                    string endTime = DateTime.UtcNow.ToString("o");

                    UploadToMongo(rating.Value, mode, placement, gameUuid, endTime, opponents);
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
                // 不设 _ratingUploaded = true，允许下次重试
            }
        }

        private void UploadToMongo(int rating, string mode, int? placement, string gameUuid, string endTime,
            List<MongoDB.Bson.BsonDocument> opponents)
        {
            if (_collection == null)
            {
                Log("MongoDB 未连接，跳过上传");
                _ratingUploaded = true;
                return;
            }

            // 最后一道防线：确保所有字段非 null
            string playerId = string.IsNullOrEmpty(_cachedPlayerId) ? "unknown" : _cachedPlayerId;
            string accountIdLo = _cachedAccountIdLo; // 字符串类型，避免大数溢出
            string region = GetRegion();
            string timestamp = DateTime.UtcNow.ToString("o");

            Task.Run(() =>
            {
                try
                {
                    var filter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("playerId", playerId);

                    // 构建新游戏记录
                    var gameRecord = new MongoDB.Bson.BsonDocument
                    {
                        { "gameUuid", gameUuid },
                        { "isLeague", false },
                        { "placement", placement.HasValue ? (MongoDB.Bson.BsonValue)new MongoDB.Bson.BsonInt32(placement.Value) : MongoDB.Bson.BsonNull.Value },
                        { "opponents", new MongoDB.Bson.BsonArray(opponents) },
                        { "endTime", endTime },
                        { "ratingChange", MongoDB.Bson.BsonNull.Value } // 先占位，管道阶段2计算
                    };

                    // 聚合管道更新：原子操作完成
                    //   1. lastRating = rating（当前分存为上局分）
                    //   2. rating = 新分数
                    //   3. ratingChange = 新分数 - 上局分
                    //   4. accountIdLo（如果获取到）
                    //   5. $concatArrays 追加分差到 ratingChanges 数组
                    //   6. 追加游戏记录到 games 数组
                    var stages = new MongoDB.Bson.BsonDocument[]
                    {
                        // Stage 1: 设置基本字段 + 计算 ratingChange
                        new MongoDB.Bson.BsonDocument("$set", new MongoDB.Bson.BsonDocument
                        {
                            { "lastRating", "$rating" },
                            { "rating", rating },
                            { "mode", mode },
                            { "timestamp", timestamp },
                            { "region", region },
                            { "gameCount", new MongoDB.Bson.BsonDocument("$add",
                                new MongoDB.Bson.BsonArray { new MongoDB.Bson.BsonDocument("$ifNull",
                                    new MongoDB.Bson.BsonArray { "$gameCount", 0 }), 1 }) },
                            { "ratingChange", new MongoDB.Bson.BsonDocument("$subtract", new MongoDB.Bson.BsonArray
                                { rating, new MongoDB.Bson.BsonDocument("$ifNull",
                                    new MongoDB.Bson.BsonArray { "$rating", rating }) }) }
                        }),
                        // Stage 2: accountIdLo（仅首次设置）
                        new MongoDB.Bson.BsonDocument("$set", new MongoDB.Bson.BsonDocument
                        {
                            { "accountIdLo", !string.IsNullOrEmpty(accountIdLo)
                                ? (MongoDB.Bson.BsonValue)new MongoDB.Bson.BsonString(accountIdLo)
                                : new MongoDB.Bson.BsonDocument("$ifNull",
                                    new MongoDB.Bson.BsonArray {
                                        new MongoDB.Bson.BsonDocument("$toString", "$accountIdLo"),
                                        MongoDB.Bson.BsonNull.Value }) },
                        }),
                        // Stage 3: 追加 ratingChanges 和 placements
                        new MongoDB.Bson.BsonDocument("$set", new MongoDB.Bson.BsonDocument
                        {
                            { "ratingChanges", new MongoDB.Bson.BsonDocument("$concatArrays",
                                new MongoDB.Bson.BsonArray
                                {
                                    new MongoDB.Bson.BsonDocument("$ifNull",
                                        new MongoDB.Bson.BsonArray { "$ratingChanges", new MongoDB.Bson.BsonArray() }),
                                    new MongoDB.Bson.BsonArray { "$ratingChange" }
                                }) },
                            { "placements", placement.HasValue
                                ? (MongoDB.Bson.BsonValue)new MongoDB.Bson.BsonDocument("$concatArrays",
                                    new MongoDB.Bson.BsonArray
                                    {
                                        new MongoDB.Bson.BsonDocument("$ifNull",
                                            new MongoDB.Bson.BsonArray { "$placements", new MongoDB.Bson.BsonArray() }),
                                        new MongoDB.Bson.BsonArray { placement.Value }
                                    })
                                : new MongoDB.Bson.BsonDocument("$ifNull",
                                    new MongoDB.Bson.BsonArray { "$placements", new MongoDB.Bson.BsonArray() }) },
                            // 追加游戏记录，把管道中计算的 ratingChange 填入 gameRecord
                            { "games", new MongoDB.Bson.BsonDocument("$concatArrays",
                                new MongoDB.Bson.BsonArray
                                {
                                    new MongoDB.Bson.BsonDocument("$ifNull",
                                        new MongoDB.Bson.BsonArray { "$games", new MongoDB.Bson.BsonArray() }),
                                    new MongoDB.Bson.BsonArray
                                    {
                                        new MongoDB.Bson.BsonDocument
                                        {
                                            { "gameUuid", gameUuid },
                                            { "isLeague", false },
                                            { "placement", placement.HasValue
                                                ? (MongoDB.Bson.BsonValue)new MongoDB.Bson.BsonInt32(placement.Value)
                                                : MongoDB.Bson.BsonNull.Value },
                                            { "opponents", new MongoDB.Bson.BsonArray(opponents) },
                                            { "endTime", endTime },
                                            { "ratingChange", "$ratingChange" } // 引用 Stage 1 计算的值
                                        }
                                    }
                                }) },
                        })
                    };
                    var update = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Pipeline(stages);

                    _collection.UpdateOne(filter, update, new MongoDB.Driver.UpdateOptions { IsUpsert = true });
                    _ratingUploaded = true;
                    string oppsStr = string.Join(", ", opponents.Select(o => o["name"].AsString + "#" + o["accountIdLo"].AsString));
                    Log($"已上传分数: {rating} ({mode}) 排名={placement?.ToString() ?? "无"} playerId={playerId} gameUuid={gameUuid} opponents=[{oppsStr}]");
                }
                catch (Exception ex)
                {
                    Log("上传 MongoDB 失败: " + ex.Message);
                    // 不设 _ratingUploaded = true，允许下次重试
                }
            });
        }

        // ── 联赛对局 ────────────────────────────────────────

        /// <summary>
        /// STEP 13 时创建 league_matches 文档（8人完整信息，placement 为 null）
        /// 8 个玩家的插件都会触发，用 upsert + SetOnInsert 防重复
        /// </summary>
        private void CreateLeagueMatch()
        {
            if (_leagueCollection == null)
            {
                Log("CreateLeagueMatch: MongoDB 未连接，跳过");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                    if (lobbyInfo?.Players == null || lobbyInfo.Players.Count == 0)
                    {
                        Log("CreateLeagueMatch: LobbyInfo 未就绪，跳过");
                        return;
                    }

                    string gameUuid = lobbyInfo.GameUuid ?? _cachedGameUuid ?? "";
                    if (string.IsNullOrEmpty(gameUuid))
                    {
                        Log("CreateLeagueMatch: gameUuid 为空，跳过");
                        return;
                    }

                    string region = GetRegion();
                    string mode = Core.Game.IsBattlegroundsDuosMatch ? "duo" : "solo";
                    string startedAt = _bgGameStartTime != DateTime.MinValue
                        ? _bgGameStartTime.ToUniversalTime().ToString("o")
                        : DateTime.UtcNow.ToString("o");

                    // 构建 players 数组（8人）
                    var playersArray = new MongoDB.Bson.BsonArray();
                    for (int i = 0; i < lobbyInfo.Players.Count; i++)
                    {
                        var p = lobbyInfo.Players[i];
                        string heroName = GetHeroName(p.HeroCardId ?? "");

                        var playerDoc = new MongoDB.Bson.BsonDocument
                        {
                            { "accountIdLo", p.AccountId?.Lo.ToString() ?? "" },
                            { "battleTag", p.Name ?? "" },
                            { "displayName", p.Name ?? "" },
                            { "heroCardId", p.HeroCardId ?? "" },
                            { "heroName", heroName },
                            { "placement", MongoDB.Bson.BsonNull.Value },
                            { "points", MongoDB.Bson.BsonNull.Value }
                        };
                        playersArray.Add(playerDoc);
                    }

                    // upsert: 第一个到达的玩家创建文档，后续玩家跳过
                    // 2.19.2 不支持 SetOnInsert，用 BsonDocument 直接构建 $setOnInsert
                    var filter = new MongoDB.Bson.BsonDocument("gameUuid", gameUuid);
                    var update = new MongoDB.Bson.BsonDocument("$setOnInsert",
                        new MongoDB.Bson.BsonDocument
                        {
                            { "players", playersArray },
                            { "region", region },
                            { "mode", mode },
                            { "startedAt", startedAt },
                            { "endedAt", MongoDB.Bson.BsonNull.Value }
                        });

                    var result = _leagueCollection.UpdateOne(filter, update,
                        new MongoDB.Driver.UpdateOptions { IsUpsert = true });

                    if (result.UpsertedId != null)
                    {
                        Log($"CreateLeagueMatch: 已创建 gameUuid={gameUuid} 玩家数={playersArray.Count} 模式={mode}");
                    }
                    else
                    {
                        Log($"CreateLeagueMatch: 文档已存在 gameUuid={gameUuid}，跳过");
                    }
                }
                catch (Exception ex)
                {
                    Log($"CreateLeagueMatch 异常: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 游戏结束时，更新自己在 league_matches 中的 placement 和 points
        /// </summary>
        private void UpdateLeaguePlacement(string accountIdLo, string gameUuid)
        {
            if (_leagueCollection == null) return;
            if (string.IsNullOrEmpty(gameUuid)) return;

            Task.Run(() =>
            {
                try
                {
                    // 获取 placement
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

                    // 计算积分: 1st=9, 2nd=7, 3rd=6, ..., 8th=1
                    int points = placement.Value == 1 ? 9 : Math.Max(1, 9 - placement.Value);

                    if (string.IsNullOrEmpty(accountIdLo))
                    {
                        Log("UpdateLeaguePlacement: accountIdLo 为空，跳过更新");
                        return;
                    }

                    // 找到自己在 players 数组中的位置，更新 placement 和 points
                    var filter = new MongoDB.Bson.BsonDocument
                    {
                        { "gameUuid", gameUuid },
                        { "players.accountIdLo", accountIdLo }
                    };
                    var update = new MongoDB.Bson.BsonDocument("$set",
                        new MongoDB.Bson.BsonDocument
                        {
                            { "players.$.placement", placement.Value },
                            { "players.$.points", points }
                        });

                    var result = _leagueCollection.UpdateOne(filter, update);

                    if (result.ModifiedCount > 0)
                    {
                        Log($"UpdateLeaguePlacement: gameUuid={gameUuid} 排名={placement.Value} 积分={points}");

                        // 检查是否所有 8 人的 placement 都已填写，若是则补 endedAt
                        CheckAndFinalizeMatch(gameUuid);
                    }
                    else
                    {
                        Log($"UpdateLeaguePlacement: 未找到匹配文档 gameUuid={gameUuid} accountIdLo={accountIdLo}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"UpdateLeaguePlacement 异常: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 检查 league_matches 文档是否所有玩家都有 placement，若是则写入 endedAt
        /// </summary>
        private void CheckAndFinalizeMatch(string gameUuid)
        {
            try
            {
                var filter = new MongoDB.Bson.BsonDocument
                {
                    { "gameUuid", gameUuid },
                    { "endedAt", MongoDB.Bson.BsonNull.Value }
                };

                // Find 在 2.19.2 需要 using MongoDB.Driver，改用等价写法
                var doc = _leagueCollection.Find(filter).FirstOrDefault();
                if (doc == null) return;

                var players = doc["players"].AsBsonArray;
                bool allDone = players.All(p => !p["placement"].IsBsonNull);

                if (allDone)
                {
                    var update = new MongoDB.Bson.BsonDocument("$set",
                        new MongoDB.Bson.BsonDocument("endedAt", DateTime.UtcNow.ToString("o")));
                    _leagueCollection.UpdateOne(
                        new MongoDB.Bson.BsonDocument("gameUuid", gameUuid),
                        update);
                    Log($"CheckAndFinalizeMatch: gameUuid={gameUuid} 对局已结束，endedAt 已写入");
                }
            }
            catch (Exception ex)
            {
                Log($"CheckAndFinalizeMatch 异常: {ex.Message}");
            }
        }

        private string GetPlayerId()
        {
            // 直接从 Player.Name 获取 BattleTag（游戏中有效，如"南怀北瑾丨少头脑#5267"）
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

            // 兜底：从 PlayerEntities 找
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
            // 从 LobbyInfo 中获取自己的 AccountId.Lo
            try
            {
                var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                if (lobbyInfo?.Players != null && !string.IsNullOrEmpty(_cachedPlayerId))
                {
                    // Player.Name 带 #tag，LobbyPlayer.Name 不带，做前缀匹配
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

        /// <summary>
        /// 收集对手信息（排除自己）
        /// </summary>
        private List<MongoDB.Bson.BsonDocument> GetOpponentInfo()
        {
            var opponents = new List<MongoDB.Bson.BsonDocument>();
            try
            {
                var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                if (lobbyInfo?.Players == null) return opponents;

                // 获取自己的名字前缀（不含 #tag）
                string myNameNoTag = _cachedPlayerId ?? "";
                int hashIdx = myNameNoTag.IndexOf('#');
                if (hashIdx > 0) myNameNoTag = myNameNoTag.Substring(0, hashIdx);

                foreach (var p in lobbyInfo.Players)
                {
                    if (p.Name == myNameNoTag) continue; // 跳过自己

                    var doc = new MongoDB.Bson.BsonDocument
                    {
                        { "name", p.Name ?? "" },
                        { "accountIdLo", p.AccountId != null ? p.AccountId.Lo.ToString() : "" }
                    };
                    opponents.Add(doc);
                }
                Log($"对手数: {opponents.Count}");
            }
            catch (Exception ex)
            {
                Log($"GetOpponentInfo 异常: {ex.Message}");
            }
            return opponents;
        }

        private void LogLobbyPlayers(bool includeHeroes = false)
        {
            try
            {
                var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                if (lobbyInfo == null) return; // lobby 尚未加载，下次再试

                var players = lobbyInfo.Players;
                if (players == null || players.Count == 0) return;

                string gameUuid = lobbyInfo.GameUuid ?? "";
                string phase = includeHeroes ? "英雄选择后" : "游戏开始";
                Log($"=== Lobby {phase} (GameUuid: {gameUuid}) ===");

                // 输出 lobby 玩家名单
                string logText = "";
                string displayText = "";
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    string name = p.Name;
                    string acctLo = p.AccountId?.Lo.ToString() ?? "?";

                    displayText += $"\n{name} {i}";

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

                // 显示 overlay（浮动窗口已禁用）
                // if (_overlay != null && !string.IsNullOrEmpty(displayText))
                // {
                //     _overlay.DisplayResult(displayText);
                // }
            }
            catch (Exception ex)
            {
                // lobby 数据可能还没准备好，不标记为已记录，下次重试
                string phase = includeHeroes ? "英雄选择后" : "游戏开始";
                Log($"LogLobbyPlayers({phase}) 等待中: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过 HearthDb 查询英雄 cardId 对应的英雄名
        /// </summary>
        private static string GetHeroName(string heroCardId)
        {
            if (string.IsNullOrEmpty(heroCardId)) return "";
            try
            {
                if (Cards.All.TryGetValue(heroCardId, out var card))
                {
                    // 优先中文名，fallback 英文
                    return card.GetLocName(HearthDb.Enums.Locale.zhCN) ?? card.Name ?? heroCardId;
                }
            }
            catch (Exception ex)
            {
                Log($"GetHeroName 异常: {ex.Message}");
            }
            return heroCardId;
        }

        /// <summary>
        /// 验证 HearthDb 是否可用，输出 3 个酒馆战棋英雄名
        /// </summary>
        public static void TestHearthDb()
        {
            try
            {
                Log("=== HearthDb 验证开始 ===");
                Log($"Cards.All 总数: {Cards.All.Count}");

                // 测试几个已知的 BG 英雄
                string[] testHeroes = {
                    "TB_BaconShop_HERO_56",   // 阿莱克丝塔萨
                    "TB_BaconShop_HERO_50",   // 苔丝·格雷迈恩
                    "BG20_HERO_202",          // 阮大师
                    "BG31_HERO_802",          // 阿塔尼斯
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
        /// 测试 MongoDB 连接
        /// </summary>
        public void TestConnection()
        {
            if (_collection == null)
            {
                Log("MongoDB 未连接");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    var testDoc = new MongoDB.Bson.BsonDocument
                    {
                        { "rating", -1 },
                        { "mode", "test" },
                        { "timestamp", DateTime.UtcNow.ToString("o") },
                        { "region", "TEST" }
                    };
                    _collection.InsertOne(testDoc);
                    _collection.DeleteOne(
                        MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("mode", "test"));
                    Log("MongoDB 连接测试成功 ✓");
                }
                catch (Exception ex)
                {
                    Log("MongoDB 连接测试失败: " + ex.Message);
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
            catch
            {
                // 日志写入失败不影响功能
            }
        }

        /// <summary>
        /// 清理旧日志：保留最近 3 天的，超过 3 天的删除
        /// </summary>
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
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // 清理失败不影响启动
            }
        }
    }
}
