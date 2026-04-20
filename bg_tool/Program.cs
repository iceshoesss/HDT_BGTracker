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
