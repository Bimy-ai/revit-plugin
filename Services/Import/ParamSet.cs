using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

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

    /// <summary>First writable double parameter from a built-in candidate list,
    /// then by display name — the pattern door/window SIZE params need, where
    /// the family decides which parameter holds "Width".</summary>
    public static bool TrySetFirst(Element e, BuiltInParameter[] candidates, string[] names, double value)
    {
        foreach (var bip in candidates)
        {
            var p = e.get_Parameter(bip);
            if (p is null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
            try { if (p.Set(value)) return true; } catch { /* try the next */ }
        }
        foreach (var name in names)
        {
            var p = e.LookupParameter(name);
            if (p is null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
            try { if (p.Set(value)) return true; } catch { /* try the next */ }
        }
        return false;
    }
}
