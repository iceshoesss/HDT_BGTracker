using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker.API;

namespace HDT_BGTracker
{
    public class RatingTracker
    {
        // ── 配置 ──────────────────────────────────────────
        private const string MongoUrl = "mongodb://YOUR_MONGO_HOST:27017";
        private const string DbName = "hearthstone";
        private const string CollectionName = "bg_ratings";

        // ── 状态 ──────────────────────────────────────────
        private bool _enabled;
        private bool _wasInBgGame;
        private DateTime _gameEndTime = DateTime.MinValue;
        private bool _ratingUploaded;
        private string _cachedPlayerId;
        private DateTime _bgGameStartTime = DateTime.MinValue;
        private static readonly TimeSpan IdReadDelay = TimeSpan.FromSeconds(3);
        private bool _lobbyLogged;
        private LobbyOverlay _overlay;

        private MongoDB.Driver.MongoClient _mongoClient;
        private MongoDB.Driver.IMongoCollection<MongoDB.Bson.BsonDocument> _collection;

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
                _mongoClient = new MongoDB.Driver.MongoClient(MongoUrl);
                var db = _mongoClient.GetDatabase(DbName);
                _collection = db.GetCollection<MongoDB.Bson.BsonDocument>(CollectionName);
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
            _overlay = null;
            Log("插件已停止");
        }

        public void SetOverlay(LobbyOverlay overlay)
        {
            _overlay = overlay;
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

                    // 记录游戏开始时间
                    if (_bgGameStartTime == DateTime.MinValue)
                        _bgGameStartTime = DateTime.Now;

                    // 延迟 3 秒后再读取 PlayerId（游戏初始化需要时间）
                    if (string.IsNullOrEmpty(_cachedPlayerId)
                        && DateTime.Now - _bgGameStartTime >= IdReadDelay)
                    {
                        _cachedPlayerId = GetPlayerId();
                    }

                    // PlayerId 获取后，尝试输出 lobby 玩家名单
                    if (!string.IsNullOrEmpty(_cachedPlayerId) && _cachedPlayerId != "unknown" && !_lobbyLogged)
                    {
                        LogLobbyPlayers();
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

                    TryUploadRating();

                    _wasInBgGame = false;
                    _gameEndTime = DateTime.MinValue;
                    _cachedPlayerId = null;
                    _bgGameStartTime = DateTime.MinValue;
                    _lobbyLogged = false;
                    _overlay?.Hide();
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

                    UploadToMongo(rating.Value, mode, placement);
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

        private void UploadToMongo(int rating, string mode, int? placement)
        {
            if (_collection == null)
            {
                Log("MongoDB 未连接，跳过上传");
                _ratingUploaded = true;
                return;
            }

            // 最后一道防线：确保所有字段非 null
            string playerId = string.IsNullOrEmpty(_cachedPlayerId) ? "unknown" : _cachedPlayerId;
            string region = GetRegion();
            string timestamp = DateTime.UtcNow.ToString("o");

            Task.Run(() =>
            {
                try
                {
                    // 注意：不存储对手ID
                    var filter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("playerId", playerId);

                    // 聚合管道更新：原子操作完成
                    //   1. lastRating = rating（当前分存为上局分）
                    //   2. rating = 新分数
                    //   3. ratingChange = 新分数 - 上局分
                    //   4. $concatArrays 追加分差到 ratingChanges 数组
                    // 首次游戏 lastRating 为 null 时用 $ifNull 兜底，分差为 0
                    var stages = new MongoDB.Bson.BsonDocument[]
                    {
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
                        })
                    };
                    var update = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Pipeline(stages);

                    _collection.UpdateOne(filter, update, new MongoDB.Driver.UpdateOptions { IsUpsert = true });
                    _ratingUploaded = true;
                    Log($"已上传分数: {rating} ({mode}) 排名={placement?.ToString() ?? "无"} playerId={playerId}");
                }
                catch (Exception ex)
                {
                    Log("上传 MongoDB 失败: " + ex.Message);
                    // 不设 _ratingUploaded = true，允许下次重试
                }
            });
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

        private void LogLobbyPlayers()
        {
            try
            {
                var lobbyInfo = Core.Game?.MetaData?.BattlegroundsLobbyInfo;
                if (lobbyInfo == null) return; // lobby 尚未加载，下次再试

                var players = lobbyInfo.Players;
                if (players == null || players.Count == 0) return;

                string gameUuid = lobbyInfo.GameUuid ?? "";
                Log($"GameUuid: {gameUuid}");

                // === 调试：Dump Player 对象所有属性 ===
                try
                {
                    var player = Core.Game?.Player;
                    if (player != null)
                    {
                        Log("=== Player 对象属性 Dump ===");
                        DumpProperties(player, "Player");
                        Log("=============================");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Player Dump 异常: {ex.Message}");
                }

                // === 调试：Dump MetaData 对象 ===
                try
                {
                    var meta = Core.Game?.MetaData;
                    if (meta != null)
                    {
                        Log("=== MetaData 对象属性 Dump ===");
                        DumpProperties(meta, "MetaData");
                        Log("==============================");
                    }
                }
                catch (Exception ex)
                {
                    Log($"MetaData Dump 异常: {ex.Message}");
                }

                // === 调试：Dump ServerInfo 对象 ===
                try
                {
                    var meta = Core.Game?.MetaData;
                    var serverInfo = meta?.ServerInfo;
                    if (serverInfo != null)
                    {
                        Log("=== ServerInfo 对象属性 Dump ===");
                        DumpProperties(serverInfo, "ServerInfo");
                        Log("================================");
                    }
                }
                catch (Exception ex)
                {
                    Log($"ServerInfo Dump 异常: {ex.Message}");
                }

                // === 调试：Dump 每个 lobby 玩家的属性 ===
                string displayText = "";
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    string name = p.Name;
                    Log($"--- Lobby Player [{i}] ---");
                    DumpProperties(p, $"LobbyPlayer[{i}]");
                    displayText += $"\n{name} {i}";
                }
                Log($"共 {players.Count} 个玩家");

                // 显示 overlay
                if (_overlay != null && !string.IsNullOrEmpty(displayText))
                {
                    _overlay.DisplayResult(displayText);
                }

                _lobbyLogged = true;
            }
            catch (Exception ex)
            {
                // lobby 数据可能还没准备好，不标记为已记录，下次重试
                Log($"LogLobbyPlayers 等待中: {ex.Message}");
            }
        }

        /// <summary>
        /// 用反射 Dump 对象的所有公共属性和字段（调试用），嵌套对象递归展开一层
        /// </summary>
        private void DumpProperties(object obj, string label, int depth = 0)
        {
            if (obj == null)
            {
                Log($"  {label}: null");
                return;
            }

            var type = obj.GetType();
            string indent = new string(' ', depth * 4);
            Log($"{indent}{label} Type: {type.FullName}");

            // 公共属性
            foreach (var prop in type.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try
                {
                    var val = prop.GetValue(obj);
                    if (val == null)
                    {
                        Log($"{indent}  {prop.Name} ({prop.PropertyType.Name}): null");
                    }
                    else if (IsSimpleType(prop.PropertyType))
                    {
                        string valStr = val.ToString();
                        if (valStr.Length > 200) valStr = valStr.Substring(0, 200) + "...";
                        Log($"{indent}  {prop.Name} ({prop.PropertyType.Name}): {valStr}");
                    }
                    else if (depth < 2) // 嵌套对象最多展开 2 层
                    {
                        Log($"{indent}  {prop.Name} ({prop.PropertyType.Name}): {{");
                        DumpProperties(val, prop.Name, depth + 1);
                        Log($"{indent}  }}");
                    }
                    else
                    {
                        Log($"{indent}  {prop.Name} ({prop.PropertyType.Name}): {val}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"{indent}  {prop.Name}: [读取异常: {ex.Message}]");
                }
            }

            // 公共字段
            foreach (var field in type.GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try
                {
                    var val = field.GetValue(obj);
                    if (val == null)
                    {
                        Log($"{indent}  {field.Name} ({field.FieldType.Name}): null");
                    }
                    else if (IsSimpleType(field.FieldType))
                    {
                        string valStr = val.ToString();
                        if (valStr.Length > 200) valStr = valStr.Substring(0, 200) + "...";
                        Log($"{indent}  {field.Name} ({field.FieldType.Name}): {valStr}");
                    }
                    else if (depth < 2)
                    {
                        Log($"{indent}  {field.Name} ({field.FieldType.Name}): {{");
                        DumpProperties(val, field.Name, depth + 1);
                        Log($"{indent}  }}");
                    }
                    else
                    {
                        Log($"{indent}  {field.Name} ({field.FieldType.Name}): {val}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"{indent}  {field.Name}: [读取异常: {ex.Message}]");
                }
            }
        }

        private static bool IsSimpleType(Type type)
        {
            return type.IsPrimitive || type.IsEnum
                || type == typeof(string) || type == typeof(decimal)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset)
                || type == typeof(TimeSpan) || type == typeof(Guid)
                || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>)
                    && IsSimpleType(type.GetGenericArguments()[0]));
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
    }
}
