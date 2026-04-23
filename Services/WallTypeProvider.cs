using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Resolves the Revit <see cref="WallType"/> every imported wall should use.
/// Per PM feedback we no longer duplicate a per-thickness type (e.g. "Generic
/// 200mm") with the BIMy type color painted on — every wall snaps to Revit's
/// stock "Concrete - 12\" Concrete" (or the nearest 12-inch concrete wall the
/// template provides). Imported walls stay branded on the instance via
/// <see cref="ImportedTag"/> so re-imports can find and replace them.
/// Must be used inside an open transaction.
/// </summary>
internal sealed class WallTypeProvider
{
    public const string ImportedTag = "BIMy import";

    // 12 inches in feet — the width we prefer for the fallback search when no
    // wall type with an exact matching name is present.
    private const double PreferredWidthFeet = 1.0;
    private const double WidthToleranceFeet = 0.05; // ±0.6"

    private readonly WallType _defaultType;

    // Kept for signature symmetry with FloorTypeProvider / CeilingTypeProvider
    // and for ImportRunner's "Auto-created types: ..." summary line. Stays
    // empty — nothing is duplicated by this resolver anymore.
    public HashSet<string> CreatedWallTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public WallTypeProvider(Document doc, ImportMaterialProvider materials)
    {
        _ = materials; // retained for API symmetry; no longer used.
        _defaultType = FindConcrete12InchType(doc)
            ?? throw new InvalidOperationException("No basic wall type exists in the project.");
    }

    public WallType Get() => _defaultType;

    // Preference order:
    //   1. Common US-imperial template names for the 12" concrete wall.
    //   2. Any basic wall type whose name contains "Concrete" and whose
    //      structural width is ≈ 12".
    //   3. The project's default wall type (whatever the user was working
    //      with before the import).
    //   4. Any basic wall type at all — last-resort so the import can still
    //      produce walls on templates that have no concrete types.
    private static WallType? FindConcrete12InchType(Document doc)
    {
        var basicWalls = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .Where(wt => wt.Kind == WallKind.Basic)
            .ToList();

        foreach (var name in new[]
                 {
                     "Concrete - 12\" Concrete",
                     "Concrete - 12\"",
                     "Basic Wall: Concrete - 12\" Concrete",
                     "Concrete 12\"",
                 })
        {
            var hit = basicWalls.FirstOrDefault(wt =>
                string.Equals(wt.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        var widthMatch = basicWalls
            .Where(wt => wt.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault(wt => Math.Abs(wt.Width - PreferredWidthFeet) <= WidthToleranceFeet);
        if (widthMatch is not null) return widthMatch;

        var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
        if (defaultId != ElementId.InvalidElementId
            && doc.GetElement(defaultId) is WallType def
            && def.Kind == WallKind.Basic)
        {
            return def;
        }

        return basicWalls.FirstOrDefault();
    }
}
