namespace MagicMirror.Native.Mirror;

/// <summary>Lightweight file logger for diagnosing the mirror pipeline (writes to app data).</summary>
public static class MirrorLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            _path ??= System.IO.Path.Combine(FileSystem.AppDataDirectory, "mirror.log");
            return _path;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}{Environment.NewLine}");
        }
        catch { /* never throw from logging */ }
    }
}
