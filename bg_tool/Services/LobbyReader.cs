namespace BgTool.Services;

using BgTool.Models;

/// <summary>通过 HearthMirror 读取大厅玩家信息</summary>
public static class LobbyReader
{
    static dynamic? _reflection;
    static bool _initAttempted;
    static bool _available;

    public static bool IsAvailable => _available;

    public static bool Init(string hdtDir)
    {
        if (_initAttempted) return _available;
        _initAttempted = true;

        try
        {
            var dllPath = Path.Combine(hdtDir, "HearthMirror.dll");
            if (!File.Exists(dllPath))
            {
                Console.WriteLine($"[HearthMirror] ⚠️ 未找到 HearthMirror.dll: {dllPath}");
                return false;
            }

            if (Environment.Is64BitProcess)
            {
                Console.WriteLine("[HearthMirror] ⚠️ 需要 32 位进程 (x86)，当前为 64 位");
                return false;
            }

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
    public static List<LobbyPlayer> GetLobbyPlayers(Game game)
    {
        if (!_available || _reflection == null) return [];

        try
        {
            var lobby = _reflection.GetBattlegroundsLobbyInfo();
            if (lobby?.Players == null || lobby!.Players.Count == 0)
            {
                Console.WriteLine("[HearthMirror] ⚠️ GetBattlegroundsLobbyInfo 返回空");
                return [];
            }

            var result = new List<LobbyPlayer>();
            foreach (var p in lobby.Players)
            {
                dynamic? accountId = p?.AccountId;
                string heroCardId = p?.HeroCardId ?? "";
                result.Add(new LobbyPlayer
                {
                    Lo = accountId?.Lo ?? 0,
                    HeroCardId = heroCardId
                });
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
