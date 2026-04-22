using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Best-effort parameter setters. Each variant silently skips when the
/// parameter is missing or read-only, and swallows Revit exceptions — used
/// for non-critical instance/type configuration where failing the whole
/// import because of one parameter is worse than skipping it.
/// </summary>
internal static class ParamSet
{
    public static void TrySet(Element e, BuiltInParameter bip, int value)
    {
        var p = e.get_Parameter(bip);
        if (p is null || p.IsReadOnly) return;
        try { p.Set(value); } catch { /* non-fatal */ }
    }

    public static void TrySet(Element e, BuiltInParameter bip, double value)
    {
        var p = e.get_Parameter(bip);
        if (p is null || p.IsReadOnly) return;
        try { p.Set(value); } catch { /* non-fatal */ }
    }

    public static void TrySet(Element e, BuiltInParameter bip, ElementId value)
    {
        var p = e.get_Parameter(bip);
        if (p is null || p.IsReadOnly) return;
        try { p.Set(value); } catch { /* non-fatal */ }
    }

    public static void TrySet(Element e, BuiltInParameter bip, string value)
    {
        var p = e.get_Parameter(bip);
        if (p is null || p.IsReadOnly) return;
        try { p.Set(value); } catch { /* non-fatal */ }
    }
}
