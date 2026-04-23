using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Resolves the Revit <see cref="FloorType"/> every imported floor should use.
/// Per PM feedback we no longer duplicate a per-thickness type or paint it with
/// the BIMy type color — every imported floor snaps to the project's default
/// floor type with its natural thickness and material. Keeping this class (and
/// the matching <see cref="CeilingTypeProvider"/>) as a thin resolver makes it
/// easy to re-introduce type-aware mapping later (e.g. BIMy-type → Revit-type
/// lookup table) without touching the builders.
/// Must be used inside an open transaction.
/// </summary>
internal sealed class FloorTypeProvider
{
    private readonly FloorType _defaultType;

    // Empty and stays empty — no types are ever created by this resolver. Kept
    // for signature compatibility with ImportRunner.BuildSummary, which unions
    // this set with the other type providers' created-types sets.
    public HashSet<string> CreatedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FloorTypeProvider(Document doc, ImportMaterialProvider materials)
    {
        _ = materials; // retained for API symmetry; no longer used.
        _defaultType = FindDefaultFloorType(doc)
            ?? throw new InvalidOperationException("No floor types exist in the project.");
    }

    public FloorType Get() => _defaultType;

    private static FloorType? FindDefaultFloorType(Document doc)
    {
        var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType);
        if (defaultId != ElementId.InvalidElementId
            && doc.GetElement(defaultId) is FloorType def)
        {
            return def;
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .FirstOrDefault(t => t.IsFoundationSlab == false);
    }
}
