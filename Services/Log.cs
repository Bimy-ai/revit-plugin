using System.Globalization;
using System.IO;

namespace RevitWallsPlugin.Services;

internal static class Log
{
    private static readonly object _gate = new();
    private static readonly string _path = ResolveLogPath();

    public static string Path => _path;

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}";
        if (ex is not null)
            line += Environment.NewLine + ex;

        try
        {
            lock (_gate)
                File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch
        {
            // Never let logging crash the command.
        }
    }

    // %LOCALAPPDATA%\BIMy\bimy.log — see BimyPaths for why not next to the DLL.
    private static string ResolveLogPath() => BimyPaths.LogFile;
}
