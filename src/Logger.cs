using System.Text;

namespace ArctisBatteryTray;

/// <summary>
/// Minimal file logger at %LocalAppData%\ArctisBatteryTray\log.txt.
/// Rotation: the file is overwritten once it exceeds 1 MB.
/// Debug level is only written when the app was started with --debug.
/// </summary>
internal static class Logger
{
    private const long MaxSizeBytes = 1024 * 1024; // 1 MB
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _debugEnabled;

    public static string LogPath => _path ??= BuildPath();

    public static void Init(bool debugEnabled)
    {
        _debugEnabled = debugEnabled;
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
        }
        catch { /* logging must never crash the app */ }

        Info($"=== ArctisBatteryTray start (debug={debugEnabled}) ===");
    }

    private static string BuildPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "ArctisBatteryTray", "log.txt");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Debug(string message)
    {
        if (_debugEnabled)
            Write("DEBUG", message);
    }

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* never throw from the logger */ }
        }

        System.Diagnostics.Debug.WriteLine(line);
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxSizeBytes)
                File.WriteAllText(LogPath, string.Empty);
        }
        catch { /* ignore */ }
    }
}
