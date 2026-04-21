using System.IO;
using System.Reflection;

namespace RevitWallsPlugin.Services;

internal static class UrlState
{
    private static readonly string _path = ResolvePath();

    public static string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var value = File.ReadAllText(_path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string url)
    {
        try { File.WriteAllText(_path, url); }
        catch { /* ignore — persistence is best-effort */ }
    }

    private static string ResolvePath()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                  ?? AppContext.BaseDirectory;
        return Path.Combine(dir, "RevitWallsPlugin.lasturl.txt");
    }
}
