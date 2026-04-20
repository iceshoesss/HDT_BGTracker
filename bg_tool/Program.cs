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
        // 日志文件（WinExe 没有控制台，Console 输出写入 bg_tool.log）
        var logDir = AppDomain.CurrentDomain.BaseDirectory;
        var logPath = Path.Combine(logDir, "bg_tool.log");
        var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);
        Console.WriteLine($"\n=== bg_tool 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

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
        Application.Run(new MainForm());
    }
}

}
