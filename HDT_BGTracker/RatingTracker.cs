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
        private const string MongoUrl = "mongodb://192.168.31.2:27017";
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
        private DateTime _lastEntityDump = DateTime.MinValue;
        private static readonly TimeSpan EntityDumpInterval = TimeSpan.FromSeconds(5);

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
                Log("插件已启动，MongoDB: " + MongoUrl);
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

                    // 记录游戏开始时间
                    if (_bgGameStartTime == DateTime.MinValue)
                        _bgGameStartTime = DateTime.Now;

                    // 延迟 3 秒后再读取 PlayerId（游戏初始化需要时间）
                    if (string.IsNullOrEmpty(_cachedPlayerId)
                        && DateTime.Now - _bgGameStartTime >= IdReadDelay)
                    {
                        _cachedPlayerId = GetPlayerId();
                    }

                    // 每5秒 dump 所有玩家实体信息
                    if (DateTime.Now - _lastEntityDump >= EntityDumpInterval)
                    {
                        _lastEntityDump = DateTime.Now;
                        DumpAllPlayers();
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
                    _lastEntityDump = DateTime.MinValue;
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
                    UploadToMongo(rating.Value, mode);
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

        private void UploadToMongo(int rating, string mode)
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
                    var filter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("playerId", playerId);
                    var update = new MongoDB.Bson.BsonDocument
                    {
                        { "$set", new MongoDB.Bson.BsonDocument
                            {
                                { "rating", rating },
                                { "mode", mode },
                                { "timestamp", timestamp },
                                { "region", region }
                            }
                        },
                        { "$inc", new MongoDB.Bson.BsonDocument { { "gameCount", 1 } } }
                    };

                    _collection.UpdateOne(filter, update, new MongoDB.Driver.UpdateOptions { IsUpsert = true });
                    _ratingUploaded = true;
                    Log($"已上传分数: {rating} ({mode}) playerId={playerId}");
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

        private void DumpAllPlayers()
        {
            Log("--- DumpAllPlayers ---");
            try
            {
                var entities = Core.Game?.Entities?.Values;
                if (entities == null) { Log("Entities 为 null"); return; }

                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (var e in entities)
                {
                    try
                    {
                        string name = e.Name;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (seen.Contains(name)) continue;
                        if (name == "调酒师鲍勃") continue;

                        // 用反射安全读取属性
                        var type = e.GetType();

                        string entityId = "";
                        var idProp = type.GetProperty("Id");
                        if (idProp != null) entityId = idProp.GetValue(e)?.ToString() ?? "";

                        string isLocal = "";
                        var localProp = type.GetProperty("IsLocalPlayer");
                        if (localProp != null)
                        {
                            var val = localProp.GetValue(e);
                            if (val is bool b && b) isLocal = " [LOCAL]";
                        }

                        // 检查 Tags 字典里是否有 PLAYER_IDENTITY (271)
                        string identity = "";
                        var tagsProp = type.GetProperty("Tags");
                        if (tagsProp != null)
                        {
                            var tags = tagsProp.GetValue(e);
                            if (tags != null)
                            {
                                // 用反射找 TryGetValue(object, out int) 重载
                                var tryGet = tags.GetType().GetMethod("TryGetValue");
                                if (tryGet != null)
                                {
                                    // 传入整数 271 作为 key（PLAYER_IDENTITY）
                                    var keyType = tryGet.GetParameters()[0].ParameterType;
                                    object key = keyType.IsEnum ? Enum.ToObject(keyType, 271) : (object)271;
                                    object[] args = { key, 0 };
                                    var found = tryGet.Invoke(tags, args);
                                    if (found is bool ok && ok && (int)args[1] > 0)
                                        identity = " [IDENTITY]";
                                }
                            }
                        }

                        Log($"  {name}{isLocal}{identity} id={entityId}");
                        seen.Add(name);
                    }
                    catch (Exception ex)
                    {
                        Log($"  实体遍历异常: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DumpAllPlayers 异常: {ex.Message}");
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
    }
}
