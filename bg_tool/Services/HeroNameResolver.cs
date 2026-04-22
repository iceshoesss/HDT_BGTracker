using System;

#nullable enable

namespace BgTool
{

/// <summary>
/// 通过 HearthDb 将 heroCardId 解析为中文英雄名
/// </summary>
public static class HeroNameResolver
{
    private static bool _initialized;
    private static bool _available;

    /// <summary>
    /// 解析 heroCardId → 中文英雄名，失败返回原 cardId
    /// </summary>
    public static string Resolve(string heroCardId)
    {
        if (string.IsNullOrEmpty(heroCardId)) return "";

        try
        {
            if (!_initialized)
            {
                _initialized = true;
                // 尝试访问 HearthDb，成功则标记可用
                var _ = HearthDb.Cards.All.Count;
                _available = true;
                Console.WriteLine("[HearthDb] ✅ 已加载，可解析英雄名");
            }

            if (_available && HearthDb.Cards.All.TryGetValue(heroCardId, out var card))
            {
                var name = card.GetLocName(HearthDb.Enums.Locale.zhCN);
                if (!string.IsNullOrEmpty(name)) return name;
                return card.Name ?? heroCardId;
            }
        }
        catch (Exception e)
        {
            if (_available)
            {
                _available = false;
                Console.WriteLine($"[HearthDb] ⚠️ 解析异常，降级: {e.Message}");
            }
        }

        return heroCardId;
    }
}

}
