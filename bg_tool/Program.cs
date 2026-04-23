using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

#nullable enable

namespace BgTool
{

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 启动保护：任何异常都写文件，避免 WinExe 静默崩溃
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            try
            {
                var crashPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bg_tool_crash.log");
                File.WriteAllText(crashPath,
                    $"=== bg_tool 启动崩溃 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n" +
                    $"OS: {Environment.OSVersion}\n" +
                    $".NET: {Environment.Version}\n" +
                    $"64-bit OS: {Environment.Is64BitOperatingSystem}\n" +
                    $"64-bit Process: {Environment.Is64BitProcess}\n" +
                    $"\n{ex}");
                MessageBox.Show(
                    $"bg_tool 启动失败，错误已写入:\n{crashPath}\n\n{ex.Message}",
                    "启动错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                MessageBox.Show($"bg_tool 启动失败:\n{ex}", "启动错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    static void Run(string[] args)
    {
        // 日志文件（WinExe 没有控制台，Console 输出写入 bg_tool.log）
        // 超过 1MB 时覆盖重写，避免无限增长
        var logDir = AppDomain.CurrentDomain.BaseDirectory;
        var logPath = Path.Combine(logDir, "bg_tool.log");
        bool overwrite = false;
        try
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024)
                overwrite = true;
        }
        catch { }
        var logWriter = new StreamWriter(logPath, append: !overwrite) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);
        Console.WriteLine($"\n=== bg_tool 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Console.WriteLine($"[系统] OS={Environment.OSVersion} | 64位OS={Environment.Is64BitOperatingSystem} | 64位进程={Environment.Is64BitProcess} | .NET={Environment.Version}");

        // HearthMirror 依赖解析
        AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) =>
        {
            var hdtDir = Environment.GetEnvironmentVariable("HDT_PATH");
            if (string.IsNullOrEmpty(hdtDir)) return null;
            var name = new AssemblyName(resolveArgs.Name).Name;
            var path = Path.Combine(hdtDir, name + ".dll");
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
            return null;
        };

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Application.Run(new MainForm());
        Console.WriteLine($"[计时] Main 退出: {sw.ElapsedMilliseconds}ms");
    }
}

}
