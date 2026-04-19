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
                return false;
            }

            // HearthMirror 需要在 32 位进程下运行
            if (Environment.Is64BitProcess)
            {
                Console.WriteLine("[HearthMirror] ⚠️ 需要 32 位进程 (x86)");
                return false;
            }

            // 加载程序集并创建 Reflection 实例
            var assembly = System.Reflection.Assembly.LoadFrom(dllPath);
            var reflType = assembly.GetType("HearthMirror.Reflection");
            if (reflType == null)
            {
                Console.WriteLine("[HearthMirror] ⚠️ 未找到 Reflection 类");
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

    /// <summary>获取大厅 8 个玩家的 Lo + HeroCardId</summary>
    public static List<LobbyPlayer> GetLobbyPlayers()
    {
        if (!_available || _reflection == null) return [];

        try
        {
            var lobby = _reflection.GetBattlegroundsLobbyInfo();
            if (lobby == null || lobby.Players == null || lobby.Players.Count == 0)
                return [];

            var result = new List<LobbyPlayer>();
            foreach (var p in lobby.Players)
            {
                long lo = p.AccountId?.Lo ?? 0;
                string hero = p.HeroCardId ?? "";
                result.Add(new LobbyPlayer { Lo = lo, HeroCardId = hero });
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
