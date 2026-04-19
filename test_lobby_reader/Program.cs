using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

// ═══════════════════════════════════════════════════════
//  测试：从 Hearthstone 进程内存读取 BG 大厅信息
//  需要：Hearthstone 正在运行 + HearthMirror.dll
//
//  编译：
//    $env:HDT_PATH = "HDT安装路径"
//    dotnet build -c Release
//
//  运行（需要 HDT 进程内 or 直接运行）：
//    bin\Release\net472\TestLobbyReader.exe
// ═══════════════════════════════════════════════════════

namespace TestLobbyReader
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== BG Lobby Reader Test ===\n");

            // 检查 Hearthstone 进程
            var hsProcess = Process.GetProcessesByName("Hearthstone");
            if (hsProcess.Length == 0)
            {
                Console.WriteLine("❌ 未找到 Hearthstone 进程，请先启动游戏");
                Console.ReadKey();
                return;
            }
            Console.WriteLine($"✅ Hearthstone 进程: PID={hsProcess[0].Id}");

            // 尝试方式 1：HearthMirror.Reflection
            Console.WriteLine("\n--- 方式 1: HearthMirror.Reflection ---");
            TryReflection();

            // 尝试方式 2：直接读进程内存
            Console.WriteLine("\n--- 方式 2: 直接读进程内存 ---");
            TryDirectMemory(hsProcess[0]);

            Console.WriteLine("\n按任意键退出...");
            Console.ReadKey();
        }

        static void TryReflection()
        {
            try
            {
                // 加载 HearthMirror
                var hmAssembly = Assembly.Load("HearthMirror");
                Console.WriteLine($"  ✅ 加载 HearthMirror: {hmAssembly.GetName().Version}");

                // 查找 Reflection 类
                var reflectionType = hmAssembly.GetType("HearthMirror.Reflection");
                if (reflectionType == null)
                {
                    Console.WriteLine("  ❌ 未找到 HearthMirror.Reflection 类");
                    return;
                }
                Console.WriteLine("  ✅ 找到 Reflection 类");

                // 列出所有公共方法
                var methods = reflectionType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                Console.WriteLine($"  📋 公共方法 ({methods.Length}):");
                foreach (var m in methods)
                {
                    Console.WriteLine($"     {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                }

                // 列出所有公共属性
                var props = reflectionType.GetProperties(BindingFlags.Public | BindingFlags.Static);
                Console.WriteLine($"  📋 公共属性 ({props.Length}):");
                foreach (var p in props)
                {
                    Console.WriteLine($"     {p.PropertyType.Name} {p.Name}");
                }

                // 尝试获取 BattlegroundsLobbyInfo
                var bgLobbyProp = reflectionType.GetProperty("BattlegroundsLobbyInfo", BindingFlags.Public | BindingFlags.Static);
                if (bgLobbyProp != null)
                {
                    Console.WriteLine($"  ✅ 找到 BattlegroundsLobbyInfo 属性，类型: {bgLobbyProp.PropertyType.FullName}");
                    var lobbyInfo = bgLobbyProp.GetValue(null);
                    if (lobbyInfo != null)
                    {
                        PrintLobbyInfo(lobbyInfo);
                    }
                    else
                    {
                        Console.WriteLine("  ⚠️ BattlegroundsLobbyInfo 为 null（可能不在游戏中）");
                    }
                }
                else
                {
                    Console.WriteLine("  ⚠️ 未找到 BattlegroundsLobbyInfo 属性");
                }

                // 尝试 GetBattlegroundsLobbyInfo 方法
                var getBgMethod = reflectionType.GetMethod("GetBattlegroundsLobbyInfo", BindingFlags.Public | BindingFlags.Static);
                if (getBgMethod != null)
                {
                    Console.WriteLine($"  ✅ 找到 GetBattlegroundsLobbyInfo 方法");
                    try
                    {
                        var result = getBgMethod.Invoke(null, null);
                        if (result != null)
                        {
                            PrintLobbyInfo(result);
                        }
                        else
                        {
                            Console.WriteLine("  ⚠️ GetBattlegroundsLobbyInfo 返回 null");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ❌ 调用失败: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ {ex.GetType().Name}: {ex.Message}");
            }
        }

        static void TryDirectMemory(Process hsProcess)
        {
            try
            {
                var hmAssembly = Assembly.Load("HearthMirror");

                // 查找 BattleGroundsLobbyInfo 类
                var bgLobbyType = hmAssembly.GetType("HearthMirror.Objects.BattleGroundsLobbyInfo");
                if (bgLobbyType == null)
                {
                    // 尝试其他命名空间
                    foreach (var t in hmAssembly.GetExportedTypes())
                    {
                        if (t.Name.Contains("Lobby") || t.Name.Contains("Battleground"))
                        {
                            Console.WriteLine($"  📋 找到类型: {t.FullName}");
                        }
                    }

                    // 尝试 LobbyPlayerList
                    var lobbyListType = hmAssembly.GetType("HearthMirror.Objects.LobbyPlayerList");
                    if (lobbyListType != null)
                    {
                        Console.WriteLine($"  ✅ 找到 LobbyPlayerList: {lobbyListType.FullName}");
                        var playersProp = lobbyListType.GetProperty("Players", BindingFlags.Public | BindingFlags.Instance);
                        if (playersProp != null)
                        {
                            Console.WriteLine($"  📋 Players 类型: {playersProp.PropertyType.FullName}");
                        }
                    }
                    return;
                }

                Console.WriteLine($"  ✅ 找到 BattleGroundsLobbyInfo: {bgLobbyType.FullName}");

                // 列出属性
                foreach (var p in bgLobbyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Console.WriteLine($"  📋 {p.PropertyType.Name} {p.Name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ {ex.GetType().Name}: {ex.Message}");
            }
        }

        static void PrintLobbyInfo(object lobbyInfo)
        {
            try
            {
                var type = lobbyInfo.GetType();

                // 获取 Players 属性
                var playersProp = type.GetProperty("Players", BindingFlags.Public | BindingFlags.Instance);
                if (playersProp == null)
                {
                    Console.WriteLine("  ❌ 未找到 Players 属性");
                    return;
                }

                var players = playersProp.GetValue(lobbyInfo) as System.Collections.IEnumerable;
                if (players == null)
                {
                    Console.WriteLine("  ❌ Players 为 null");
                    return;
                }

                Console.WriteLine("\n  📊 大厅玩家:");
                int count = 0;
                foreach (var player in players)
                {
                    count++;
                    var pType = player.GetType();
                    string name = pType.GetProperty("Name")?.GetValue(player)?.ToString() ?? "?";
                    var accountId = pType.GetProperty("AccountId")?.GetValue(player);
                    string hi = "?", lo = "?";
                    if (accountId != null)
                    {
                        hi = accountId.GetType().GetProperty("Hi")?.GetValue(accountId)?.ToString() ?? "?";
                        lo = accountId.GetType().GetProperty("Lo")?.GetValue(accountId)?.ToString() ?? "?";
                    }
                    string heroCardId = pType.GetProperty("HeroCardId")?.GetValue(player)?.ToString() ?? "?";

                    Console.WriteLine($"     [{count}] Name={name}, AccountId(Hi={hi}, Lo={lo}), Hero={heroCardId}");
                }
                Console.WriteLine($"  📊 共 {count} 个玩家");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 解析失败: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
    }
}
