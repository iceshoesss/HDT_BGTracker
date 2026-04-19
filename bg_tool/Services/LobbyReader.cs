namespace BgTool.Services;

using BgTool.Models;

/// <summary>通过 HearthMirror 读取大厅玩家信息</summary>
public static class LobbyReader
{
    private static dynamic? _reflection;
    private static bool _initAttempted;
    private static bool _available;

    public static bool IsAvailable => _available;

    /// <summary>初始化 HearthMirror</summary>
    public static bool Init(string hdtDir)
    {
        if (_initAttempted) return _available;
        _initAttempted = true;

        try
        {
            var dllPath = Path.Combine(hdtDir, "HearthMirror.dll");
            if (!File.Exists(dllPath))
            {
                Console.WriteLine("[HearthMirror] ⚠️ 未找到 HearthMirror.dll");
                Console.WriteLine($"   查找路径: {dllPath}");
                return false;
            }

            // HearthMirror 需要在 32 位进程下运行
            if (Environment.Is64BitProcess)
            {
                Console.WriteLine("[HearthMirror] ⚠️ 需要 32 位进程 (x86)，当前为 64 位");
                Console.WriteLine("   用法: dotnet publish -r win-x86 或设置 RuntimeIdentifier");
                return false;
            }

            // 加载程序集并创建 Reflection 实例
            var assembly = System.Reflection.Assembly.LoadFrom(dllPath);
            var reflType = assembly.GetType("HearthMirror.Reflection");
            if (reflType == null)
            {
                Console.WriteLine("[HearthMirror] ⚠️ 未找到 Reflection 类");
                Console.WriteLine($"   程序集类型: {string.Join(", ", assembly.GetTypes().Select(t => t.FullName))}");
                return false;
            }

            _reflection = Activator.CreateInstance(reflType);
            _available = true;
            Console.WriteLine("[HearthMirror] ✅ 已加载，可获取对手 Lo");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HearthMirror] ⚠️ 不可用: {ex.Message}");
            return false;
        }
    }

    /// <summary>获取大厅 8 个玩家的 Lo + HeroCardId，匹配到已知英雄则填入 HeroName</summary>
    public static List<LobbyPlayer> GetLobbyPlayers(Game game)
    {
        if (!_available || _reflection == null) return [];

        try
        {
            var lobby = _reflection.GetBattlegroundsLobbyInfo();
            if (lobby == null || lobby.Players == null || lobby.Players.Count == 0)
            {
                Console.WriteLine("[HearthMirror] ⚠️ GetBattlegroundsLobbyInfo 返回空");
                return [];
            }

            var result = new List<LobbyPlayer>();
            for (int i = 0; i < lobby.Players.Count; i++)
            {
                var p = lobby.Players[i];
                long lo = p.AccountId?.Lo ?? 0;
                string heroCardId = p.HeroCardId ?? "";
                string heroName = "";

                // 尝试通过 heroCardId 匹配已有英雄名
                if (!string.IsNullOrEmpty(heroCardId))
                {
                    foreach (var hero in game.AllHeroes.Values)
                    {
                        if (hero.CardId == heroCardId)
                        {
                            heroName = hero.HeroName;
                            break;
                        }
                    }
                }

                result.Add(new LobbyPlayer { Lo = lo, HeroCardId = heroCardId });
            }

            Console.WriteLine($"[HearthMirror] 📋 获取到 {result.Count} 个玩家");
            for (int i = 0; i < result.Count; i++)
            {
                var lp = result[i];
                Console.WriteLine($"   [{i + 1}] Lo={lp.Lo}, Hero={lp.HeroCardId}");
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HearthMirror] 读取失败: {ex.Message}");
            return [];
        }
    }
}
