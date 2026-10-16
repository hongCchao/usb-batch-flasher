using System.IO;
using System.Text;
using System.Text.Json;

namespace UsbBatchTool.Services;

public static class AppPaths
{
    public static string Base => string.IsNullOrEmpty(typeof(AppPaths).Assembly.Location) ? AppContext.BaseDirectory : Path.GetDirectoryName(typeof(AppPaths).Assembly.Location)!;
    public static string Cache => Path.Combine(Base, "cache");
    public static string Logs => Path.Combine(Base, "logs");
    public static string Config => Path.Combine(Base, "config");
    public static void EnsureDirectories()
    {
        foreach (var path in new[] { Cache, Logs, Config }) Directory.CreateDirectory(path);
    }
}

public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(AppPaths.Config, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string? LoadWarning { get; private set; }
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? throw new InvalidDataException("配置为空");
            settings.Ports ??= [];
            settings.RecentFiles ??= [];
            settings.ConcurrentJobs = Math.Clamp(settings.ConcurrentJobs, 1, 7);
            settings.RefreshSeconds = Math.Clamp(settings.RefreshSeconds, 2, 60);
            for (int i = 0; i < settings.Ports.Count; i++)
            {
                var p = settings.Ports[i];
                if (p.Id == Guid.Empty) p.Id = Guid.NewGuid();
                p.LocationPaths ??= [];
                p.FallbackDeviceKey ??= "";
                p.Name = string.IsNullOrWhiteSpace(p.Name) ? $"端口 {i + 1}" : p.Name;
            }
            return settings;
        }
        catch (Exception ex)
        {
            LoadWarning = "配置文件无法读取，已加载默认设置；原文件保留。" + ex.Message;
            return new AppSettings();
        }
    }
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.Config);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));
        File.Move(temp, _path, true);
    }
}

public sealed record LogEntry(DateTime Time, string Level, string Port, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff}\t{Level}\t{Port}\t{Message}";
}

public sealed class LogService
{
    private readonly object _gate = new();
    public string CurrentLogPath { get; private set; } = Path.Combine(AppPaths.Logs, $"session_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
    public event Action<LogEntry>? EntryAdded;
    public void BeginTask() { lock (_gate) CurrentLogPath = Path.Combine(AppPaths.Logs, $"task_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log"); }
    public void Write(string message, string port = "系统", string level = "信息")
    {
        var entry = new LogEntry(DateTime.Now, level, port, message);
        lock (_gate)
        {
            Directory.CreateDirectory(AppPaths.Logs);
            File.AppendAllText(CurrentLogPath, entry + Environment.NewLine, new UTF8Encoding(false));
        }
        EntryAdded?.Invoke(entry);
    }
}
