namespace BgTool.Services;

using BgTool.Parser;

/// <summary>文件 tail 监控（从 Python bg_parser 移植）</summary>
public class FileMonitor : IDisposable
{
    private readonly string _initialPath;
    private string _currentPath;
    private long _lastKnownLength;
    private readonly int _fileCheckEvery;
    private int _fileCheckCounter;
    private StreamReader? _reader;

    public FileMonitor(string logPath, int fileCheckEvery = 100)
    {
        _initialPath = logPath;
        _currentPath = logPath;
        _fileCheckEvery = fileCheckEvery;
    }

    /// <summary>读取所有新行。依靠 StreamReader 自然读取，不做手动 position 追踪。</summary>
    public List<string> ReadNewLines()
    {
        var lines = new List<string>();
        try
        {
            // 检测文件被截断或重建（长度回退）
            if (_reader != null)
            {
                try
                {
                    var currentLen = _reader.BaseStream.Length;
                    if (currentLen < _lastKnownLength)
                    {
                        // 文件被截断或重建
                        _reader.Dispose();
                        _reader = null;
                    }
                    _lastKnownLength = currentLen;
                }
                catch
                {
                    _reader.Dispose();
                    _reader = null;
                }
            }

            if (_reader == null)
            {
                _reader = new StreamReader(
                    new FileStream(_currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                    System.Text.Encoding.UTF8);
                _lastKnownLength = _reader.BaseStream.Length;
            }

            string? line;
            while ((line = _reader.ReadLine()) != null)
                lines.Add(line);

            // 更新已知长度（文件可能增长了）
            try { _lastKnownLength = _reader.BaseStream.Length; } catch { }
        }
        catch (FileNotFoundException)
        {
            // 文件消失
        }
        return lines;
    }

    /// <summary>检查是否有新的日志文件夹</summary>
    public string? CheckNewLogFile()
    {
        _fileCheckCounter++;
        if (_fileCheckCounter < _fileCheckEvery) return null;
        _fileCheckCounter = 0;

        var currentDir = Path.GetDirectoryName(_currentPath)!;
        var parent = Path.GetDirectoryName(currentDir);
        var basename = Path.GetFileName(currentDir);
        var logsDir = basename.StartsWith("Hearthstone_") ? parent! : currentDir;

        if (!Directory.Exists(logsDir)) return null;

        string? newestPath = null;
        DateTime newestMtime = DateTime.MinValue;

        try { newestMtime = File.GetLastWriteTime(_currentPath); } catch { }

        foreach (var folder in Directory.GetDirectories(logsDir, "Hearthstone_*"))
        {
            var p = Path.Combine(folder, "Power.log");
            if (File.Exists(p) && Path.GetFullPath(p) != Path.GetFullPath(_currentPath))
            {
                var mtime = File.GetLastWriteTime(p);
                if (mtime > newestMtime) { newestMtime = mtime; newestPath = p; }
            }
        }

        var rootLog = Path.Combine(logsDir, "Power.log");
        if (File.Exists(rootLog) && Path.GetFullPath(rootLog) != Path.GetFullPath(_currentPath))
        {
            var mtime = File.GetLastWriteTime(rootLog);
            if (mtime > newestMtime) newestPath = rootLog;
        }

        return newestPath;
    }

    /// <summary>切换到新日志文件</summary>
    public void SwitchTo(string newPath)
    {
        _reader?.Dispose();
        _reader = null;
        _currentPath = newPath;
        _lastKnownLength = 0;
    }

    /// <summary>跳到最后一个 CREATE_GAME 位置并准备读取</summary>
    public void SeekToLastCreateGame()
    {
        var pos = LogParser.FindLastCreateGamePos(_currentPath);
        _reader?.Dispose();
        var fs = new FileStream(_currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _reader = new StreamReader(fs, System.Text.Encoding.UTF8);
        _reader.BaseStream.Seek(pos, SeekOrigin.Begin);
        _lastKnownLength = fs.Length;
    }

    public void Dispose()
    {
        _reader?.Dispose();
    }
}
