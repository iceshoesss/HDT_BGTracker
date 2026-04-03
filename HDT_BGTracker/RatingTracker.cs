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
        private DateTime _lastIdCheck = DateTime.MinValue;
        private static readonly TimeSpan IdCheckInterval = TimeSpan.FromSeconds(5);

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

                    // 每 5 秒尝试获取一次 PlayerId
                    if (string.IsNullOrEmpty(_cachedPlayerId) || _cachedPlayerId == "unknown")
                    {
                        if (DateTime.Now - _lastIdCheck >= IdCheckInterval)
                        {
                            _lastIdCheck = DateTime.Now;
                            _cachedPlayerId = GetPlayerId();
                        }
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
                    _lastIdCheck = DateTime.MinValue;
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
            Log("=== GetPlayerId 开始 ===");

            // 方法1：遍历 Config.Instance 所有字符串属性，找 BattleTag / Account 相关
            try
            {
                var configType = typeof(Hearthstone_Deck_Tracker.Config);
                var instanceProp = configType.GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                Log($"GetPlayerId: Config.Instance prop = {(instanceProp != null ? "found" : "null")}");

                if (instanceProp != null)
                {
                    var config = instanceProp.GetValue(null);
                    Log($"GetPlayerId: Config value = {(config != null ? config.GetType().FullName : "null")}");

                    if (config != null)
                    {
                        // 列出 Config 的所有属性，找出包含账号信息的
                        foreach (var prop in config.GetType().GetProperties(
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            try
                            {
                                // 只打印字符串类型的和看起来跟账号相关的
                                if (prop.PropertyType == typeof(string))
                                {
                                    var val = prop.GetValue(config)?.ToString();
                                    if (!string.IsNullOrEmpty(val))
                                    {
                                        string lower = prop.Name.ToLower();
                                        if (lower.Contains("name") || lower.Contains("tag") ||
                                            lower.Contains("account") || lower.Contains("battle") ||
                                            lower.Contains("player") || lower.Contains("user"))
                                        {
                                            Log($"GetPlayerId: Config.{prop.Name} = {val}");
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        // 尝试 BattleTag
                        var btProp = config.GetType().GetProperty("BattleTag");
                        if (btProp != null)
                        {
                            string btVal = btProp.GetValue(config)?.ToString();
                            if (!string.IsNullOrEmpty(btVal) && btVal != "-1")
                            {
                                Log($"GetPlayerId: ✓ Config.BattleTag = {btVal}");
                                return btVal;
                            }
                            else
                            {
                                Log($"GetPlayerId: Config.BattleTag = '{btVal}' (无效)");
                            }
                        }
                        else
                        {
                            Log("GetPlayerId: Config 没有 BattleTag 属性");
                        }

                        // 尝试其他候选
                        string[] configCandidates = { "AccountName", "PlayerName", "HearthstoneAccount", "DisplayName" };
                        foreach (var cName in configCandidates)
                        {
                            var cProp = config.GetType().GetProperty(cName);
                            if (cProp == null) continue;
                            string cVal = cProp.GetValue(config)?.ToString();
                            if (!string.IsNullOrEmpty(cVal) && cVal != "-1")
                            {
                                Log($"GetPlayerId: ✓ Config.{cName} = {cVal}");
                                return cVal;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetPlayerId: Config 读取异常: {ex.Message}");
            }

            // 方法2：从游戏实体中找玩家名（BATTLETAG 在 entity tags 里）
            try
            {
                var game = Core.Game;
                if (game?.Player != null)
                {
                    // 遍历 Player 的实体，找 PLAYER_IDENTITY 或 BATTLETAG
                    var entities = game.Player.PlayerEntities?.ToList();
                    if (entities != null)
                    {
                        foreach (var entity in entities)
                        {
                            try
                            {
                                var name = entity.Name;
                                if (!string.IsNullOrEmpty(name))
                                {
                                    Log($"GetPlayerId: PlayerEntity Name = {name}");
                                }

                                // 通过反射读取 entity 的 tags，找 GameTag.PLAYER_IDENTITY
                                var entityType = entity.GetType();
                                var tagsProp = entityType.GetProperty("Tags");
                                if (tagsProp != null)
                                {
                                    var tags = tagsProp.GetValue(entity);
                                    if (tags != null)
                                    {
                                        // 尝试转成 Dictionary 或类似结构
                                        var tagsStr = tags.ToString();
                                        if (!string.IsNullOrEmpty(tagsStr) && tagsStr.Length < 500)
                                        {
                                            Log($"GetPlayerId: Entity tags = {tagsStr}");
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    // 也试试 Opponent
                    var opponent = game.Opponent;
                    if (opponent != null)
                    {
                        var oppName = opponent.Name;
                        if (!string.IsNullOrEmpty(oppName))
                        {
                            Log($"GetPlayerId: Opponent.Name = {oppName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetPlayerId: GameEntities 读取异常: {ex.Message}");
            }

            // 方法3：Core.Game.Player 兜底
            try
            {
                var player = Core.Game?.Player;
                if (player != null)
                {
                    // Name 和 Id 已在日志中看到为空/-1，这里只检查是否有新属性出现
                    var type = player.GetType();
                    foreach (var prop in type.GetProperties(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        try
                        {
                            if (prop.PropertyType == typeof(string))
                            {
                                var val = prop.GetValue(player)?.ToString();
                                if (!string.IsNullOrEmpty(val))
                                {
                                    string lower = prop.Name.ToLower();
                                    if (lower.Contains("name") || lower.Contains("id") ||
                                        lower.Contains("tag") || lower.Contains("account") ||
                                        lower.Contains("battle"))
                                    {
                                        Log($"GetPlayerId: Player.{prop.Name} = {val}");
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetPlayerId: Player 读取异常: {ex.Message}");
            }

            Log("=== GetPlayerId: 未找到有效 ID ===");
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
