using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace BgTool
{

/// <summary>
/// 自动更新服务（GitHub Releases）
/// </summary>
public static class UpdateChecker
{
    private const string REPO_OWNER = "iceshoesss";
    private const string REPO_NAME = "HDT_BGTracker";
    private const string GITHUB_API = "https://api.github.com/repos/{0}/{1}/releases/latest";
    private const string DOWNLOAD_MIRROR = "https://ghproxy.com/";  // 下载镜像前缀

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    static UpdateChecker()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "bg_tool-updater");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }

    /// <summary>
    /// 检查更新结果
    /// </summary>
    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string LatestVersion { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string MirrorUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
    }

    /// <summary>
    /// 获取当前版本
    /// </summary>
    public static Version GetCurrentVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v ?? new Version(0, 0, 0);
    }

    /// <summary>
    /// 检查是否有新版本
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        var result = new UpdateInfo { CurrentVersion = GetCurrentVersion().ToString(3) };

        try
        {
            var url = string.Format(GITHUB_API, REPO_OWNER, REPO_NAME);
            var json = await _http.GetStringAsync(url);

            // 解析 tag_name
            var tagName = ExtractJsonString(json, "tag_name");  // e.g. "v0.5.7"
            if (string.IsNullOrEmpty(tagName))
            {
                Console.WriteLine("[更新] ⚠️ 无法获取 release tag");
                return result;
            }

            var latestVer = tagName.TrimStart('v');
            result.LatestVersion = latestVer;

            // 版本比较
            Version current, latest;
            if (!Version.TryParse(result.CurrentVersion, out current) ||
                !Version.TryParse(latestVer, out latest))
            {
                Console.WriteLine($"[更新] ⚠️ 版本解析失败: current={result.CurrentVersion} latest={latestVer}");
                return result;
            }

            if (latest <= current)
            {
                Console.WriteLine($"[更新] ✅ 已是最新版本 {result.CurrentVersion}");
                return result;
            }

            result.HasUpdate = true;

            // 解析 body（release notes）
            result.ReleaseNotes = ExtractJsonString(json, "body");
            if (result.ReleaseNotes.Length > 500)
                result.ReleaseNotes = result.ReleaseNotes.Substring(0, 500) + "...";

            // 查找 zip 资源
            var assets = ParseAssets(json);
            foreach (var asset in assets)
            {
                if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    result.DownloadUrl = asset.Url;
                    result.FileName = asset.Name;
                    result.FileSize = asset.Size;
                    // 生成镜像 URL
                    result.MirrorUrl = DOWNLOAD_MIRROR + asset.Url;
                    break;
                }
            }

            Console.WriteLine($"[更新] 🔔 发现新版本 v{latestVer}（当前 {result.CurrentVersion}）");
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("[更新] ⚠️ 检查更新超时");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[更新] ⚠️ 网络错误: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[更新] ❌ 检查更新失败: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 下载更新文件到临时目录
    /// </summary>
    /// <returns>下载后的文件路径，失败返回 null</returns>
    public static async Task<string?> DownloadAsync(UpdateInfo info, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.MirrorUrl) && string.IsNullOrEmpty(info.DownloadUrl))
            return null;

        var tempDir = Path.Combine(Path.GetTempPath(), "bg_tool_update");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, info.FileName);

        // 优先用镜像，失败回退原站
        var urls = new List<string>();
        if (!string.IsNullOrEmpty(info.MirrorUrl)) urls.Add(info.MirrorUrl);
        if (!string.IsNullOrEmpty(info.DownloadUrl)) urls.Add(info.DownloadUrl);

        foreach (var url in urls)
        {
            try
            {
                Console.WriteLine($"[更新] ⬇️ 开始下载: {url}");
                using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? 0;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long downloaded = 0;
                        int read;
                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, read, ct);
                            downloaded += read;
                            if (total > 0)
                            {
                                var pct = (int)(downloaded * 100 / total);
                                progress?.Report(pct);
                            }
                        }
                    }
                }

                Console.WriteLine($"[更新] ✅ 下载完成: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[更新] ⚠️ 下载失败 ({url}): {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// 生成更新脚本并启动（退出当前进程 → 替换 → 重启）
    /// </summary>
    public static void ApplyUpdate(string downloadedFile)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(currentExe))
        {
            Console.WriteLine("[更新] ❌ 无法获取当前 exe 路径");
            return;
        }

        var backupExe = currentExe + ".bak";
        var batPath = Path.Combine(Path.GetTempPath(), "bg_tool_update.bat");

        var bat = new StringBuilder();
        bat.AppendLine("@echo off");
        bat.AppendLine("chcp 65001 >nul");
        bat.AppendLine($"echo [更新] 等待 bg_tool 退出...");
        bat.AppendLine($"timeout /t 2 /nobreak >nul");
        bat.AppendLine($"del /f /q \"{backupExe}\" 2>nul");
        bat.AppendLine($"ren \"{currentExe}\" \"{Path.GetFileName(backupExe)}\"");
        bat.AppendLine($"copy /y \"{downloadedFile}\" \"{currentExe}\"");
        bat.AppendLine($"if errorlevel 1 (");
        bat.AppendLine($"    echo [更新] 替换失败，回滚...");
        bat.AppendLine($"    ren \"{Path.GetFileName(backupExe)}\" \"{Path.GetFileName(currentExe)}\"");
        bat.AppendLine($") else (");
        bat.AppendLine($"    del /f /q \"{backupExe}\"");
        bat.AppendLine($"    echo [更新] 更新完成，启动 bg_tool...");
        bat.AppendLine($"    start \"\" \"{currentExe}\"");
        bat.AppendLine(")");
        bat.AppendLine($"del /f /q \"{downloadedFile}\"");
        bat.AppendLine($"del /f /q \"%~f0\"");

        File.WriteAllText(batPath, bat.ToString(), Encoding.Default);

        Console.WriteLine("[更新] 🚀 启动更新脚本，退出 bg_tool...");
        Process.Start(new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        });

        Application.Exit();
    }

    // ── JSON 辅助（复用 ApiClient 风格）──

    private static string ExtractJsonString(string json, string key)
    {
        var search = $"\"{key}\":\"";
        var idx = json.IndexOf(search);
        if (idx < 0) return "";
        idx += search.Length;
        var end = idx;
        while (end < json.Length)
        {
            if (json[end] == '"' && json[end - 1] != '\\') break;
            end++;
        }
        return json.Substring(idx, end - idx).Replace("\\\"", "\"").Replace("\\n", "\n");
    }

    private struct AssetInfo
    {
        public string Name;
        public string Url;
        public long Size;
    }

    private static List<AssetInfo> ParseAssets(string json)
    {
        var result = new List<AssetInfo>();
        // 简单解析 assets 数组中的 browser_download_url
        var assetsIdx = json.IndexOf("\"assets\"");
        if (assetsIdx < 0) return result;

        var searchArea = json.Substring(assetsIdx);
        var urlPattern = "\"browser_download_url\":\"";
        var namePattern = "\"name\":\"";
        var sizePattern = "\"size\":";

        int pos = 0;
        while (pos < searchArea.Length)
        {
            var nameIdx = searchArea.IndexOf(namePattern, pos);
            if (nameIdx < 0) break;
            nameIdx += namePattern.Length;
            var nameEnd = searchArea.IndexOf('"', nameIdx);
            var name = searchArea.Substring(nameIdx, nameEnd - nameIdx);

            var urlIdx = searchArea.IndexOf(urlPattern, pos);
            string url = "";
            if (urlIdx >= 0 && urlIdx < nameEnd + 200)
            {
                urlIdx += urlPattern.Length;
                var urlEnd = searchArea.IndexOf('"', urlIdx);
                url = searchArea.Substring(urlIdx, urlEnd - urlIdx);
            }

            var sizeIdx = searchArea.IndexOf(sizePattern, pos);
            long size = 0;
            if (sizeIdx >= 0 && sizeIdx < nameEnd + 200)
            {
                sizeIdx += sizePattern.Length;
                var sizeEnd = sizeIdx;
                while (sizeEnd < searchArea.Length && char.IsDigit(searchArea[sizeEnd]))
                    sizeEnd++;
                long.TryParse(searchArea.Substring(sizeIdx, sizeEnd - sizeIdx), out size);
            }

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                result.Add(new AssetInfo { Name = name, Url = url, Size = size });

            pos = nameEnd + 1;
        }

        return result;
    }
}

}
