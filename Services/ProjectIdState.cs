using System.IO;
using System.Reflection;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal static class ProjectIdState
{
    private static readonly string _dir = ResolveDir();

    public static string? Load(BimyEnvironment env)
    {
        try
        {
            var path = PathFor(env);
            if (!File.Exists(path)) return null;
            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(BimyEnvironment env, string projectId)
    {
        try { File.WriteAllText(PathFor(env), projectId); }
        catch { /* best effort */ }
    }

    private static string PathFor(BimyEnvironment env)
    {
        var suffix = env.ToString().ToLowerInvariant();
        return Path.Combine(_dir, $"RevitWallsPlugin.lastProjectId.{suffix}.txt");
    }

    private static string ResolveDir()
        => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
           ?? AppContext.BaseDirectory;
}
