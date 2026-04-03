using System;
using System.IO;
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

                    // 持续尝试获取 PlayerId，直到拿到非 null 值
                    if (string.IsNullOrEmpty(_cachedPlayerId))
                        _cachedPlayerId = GetPlayerId();
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
                if (string.IsNullOrEmpty(_cachedPlayerId))
                {
                    _cachedPlayerId = GetPlayerId();
                    if (string.IsNullOrEmpty(_cachedPlayerId))
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
            try
            {
                // 方法1：从 Config.Instance 获取 BattleTag
                try
                {
                    var configType = typeof(Hearthstone_Deck_Tracker.Config);
                    var instanceProp = configType.GetProperty("Instance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (instanceProp != null)
                    {
                        var config = instanceProp.GetValue(null);
                        if (config != null)
                        {
                            // 尝试 BattleTag
                            var btProp = config.GetType().GetProperty("BattleTag");
                            if (btProp != null)
                            {
                                string btVal = btProp.GetValue(config)?.ToString();
                                if (!string.IsNullOrEmpty(btVal) && btVal != "-1")
                                {
                                    Log($"GetPlayerId: Config.BattleTag = {btVal}");
                                    return btVal;
                                }
                            }

                            // 尝试 AccountName / PlayerName / HearthstoneAccount
                            string[] configCandidates = { "AccountName", "PlayerName", "HearthstoneAccount" };
                            foreach (var cName in configCandidates)
                            {
                                var cProp = config.GetType().GetProperty(cName);
                                if (cProp == null) continue;
                                string cVal = cProp.GetValue(config)?.ToString();
                                if (!string.IsNullOrEmpty(cVal) && cVal != "-1")
                                {
                                    Log($"GetPlayerId: Config.{cName} = {cVal}");
                                    return cVal;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"GetPlayerId: Config 读取失败: {ex.Message}");
                }

                // 方法2：从 Core.Game.Player 兜底（列出属性便于调试）
                var player = Core.Game?.Player;
                if (player != null)
                {
                    var type = player.GetType();
                    Log($"GetPlayerId: Player 类型 = {type.FullName}");

                    // 调试：列出所有公共属性
                    foreach (var prop in type.GetProperties(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        try
                        {
                            var val = prop.GetValue(player)?.ToString() ?? "null";
                            Log($"  {prop.Name} [{prop.PropertyType.Name}] = {val}");
                        }
                        catch (Exception ex)
                        {
                            Log($"  {prop.Name} = 读取失败: {ex.Message}");
                        }
                    }

                    // 优先级：Name > AccountId > Id
                    string[] candidates = { "Name", "AccountId", "Id" };
                    foreach (var cName in candidates)
                    {
                        var prop = type.GetProperty(cName);
                        if (prop == null) continue;
                        var val = prop.GetValue(player)?.ToString();
                        if (!string.IsNullOrEmpty(val) && val != "-1")
                        {
                            Log($"GetPlayerId: Player.{cName} = {val}");
                            return val;
                        }
                    }
                }
                else
                {
                    Log("GetPlayerId: Player 对象为 null");
                }
            }
            catch (Exception ex)
            {
                Log("GetPlayerId 异常: " + ex.Message);
            }
            Log("GetPlayerId: 所有方法都失败，返回 unknown");
            return "unknown";
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
