namespace BgTool.Services;

/// <summary>文件 tail 监控（从 Python bg_parser 移植）</summary>
public class FileMonitor : IDisposable
{
    private readonly string _initialPath;
    private string _currentPath;
    private long _position;
    private readonly int _fileCheckEvery;
    private int _fileCheckCounter;
    private StreamReader? _reader;

    public FileMonitor(string logPath, int fileCheckEvery = 100)
    {
        _initialPath = logPath;
        _currentPath = logPath;
        _fileCheckEvery = fileCheckEvery;
    }

    /// <summary>读取所有新行</summary>
    public List<string> ReadNewLines()
    {
        var lines = new List<string>();
        try
        {
            if (_reader == null || _reader.BaseStream.Length < _position)
            {
                // 文件被截断或重新创建
                _reader?.Dispose();
                _reader = new StreamReader(new FileStream(_currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                    System.Text.Encoding.UTF8);
                _reader.BaseStream.Seek(_position, SeekOrigin.Begin);
            }

            string? line;
            while ((line = _reader.ReadLine()) != null)
                lines.Add(line);
            _position = _reader.BaseStream.Position;
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
        _position = 0;
    }

    /// <summary>跳到最后一个 CREATE_GAME 位置并读取已有内容</summary>
    public void SeekToLastCreateGame()
    {
        _position = LogParser.FindLastCreateGamePos(_currentPath);
        _reader?.Dispose();
        _reader = new StreamReader(new FileStream(_currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
            System.Text.Encoding.UTF8);
        _reader.BaseStream.Seek(_position, SeekOrigin.Begin);
    }

    public void Dispose()
    {
        _reader?.Dispose();
    }
}
