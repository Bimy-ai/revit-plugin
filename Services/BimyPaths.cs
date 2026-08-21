using System.IO;

namespace BimyRevit.Services;

/// <summary>
/// Where the add-in keeps everything it writes.
///
/// NOT next to the DLL. The installer can deploy machine-wide into
/// <c>%ProgramData%\Autodesk\Revit\Addins\&lt;year&gt;\BIMy\</c>, which a normal
/// user cannot write to — so a log, a saved session or a pull cache placed
/// beside the assembly silently vanishes for exactly the users who most need it
/// to work. Everything lives under <c>%LOCALAPPDATA%\BIMy</c> instead: writable
/// without elevation, per-Windows-user (which the DPAPI-encrypted token already
/// requires), and shared by every installed Revit year so connecting once in
/// Revit 2025 also connects Revit 2026.
/// </summary>
internal static class BimyPaths
{
    /// <summary>%LOCALAPPDATA%\BIMy — created on first use.</summary>
    public static string Root { get; } = EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMy"));

    /// <summary>Downloaded IFC + the converted .rvt, one folder per project.</summary>
    public static string ModelsRoot { get; } = EnsureDir(Path.Combine(Root, "models"));

    /// <summary>
    /// Where a pulled model lands by default: Documents\BIMy Models. A user's
    /// building belongs somewhere they can find it again, not in AppData.
    /// </summary>
    public static string DefaultSaveRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BIMy Models");

    public static string SessionFile => Path.Combine(Root, "session.json");
    public static string PullCacheFile => Path.Combine(Root, "pulls.json");
    public static string LogFile => Path.Combine(Root, "bimy.log");

    /// <summary>Scratch folder for one project's download (IFC + intermediate .rvt).</summary>
    public static string ModelDir(string projectId) => EnsureDir(Path.Combine(ModelsRoot, Sanitize(projectId)));

    /// <summary>Replaces characters Windows forbids in a file name with '_'.</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var cleaned = new string(chars).Trim('.', ' ');
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }

    private static string EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* best effort — callers all tolerate failure */ }
        return path;
    }
}
