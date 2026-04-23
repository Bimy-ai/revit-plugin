using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Resolves the Revit <see cref="CeilingType"/> every imported ceiling should
/// use. Mirrors <see cref="FloorTypeProvider"/>: one default ceiling type for
/// every imported instance, no per-color duplication, no overridden thickness.
/// Must be used inside an open transaction.
/// </summary>
internal sealed class CeilingTypeProvider
{
    private readonly CeilingType _defaultType;

    public HashSet<string> CreatedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CeilingTypeProvider(Document doc, ImportMaterialProvider materials)
    {
        _ = materials;
        _defaultType = FindDefaultCeilingType(doc)
            ?? throw new InvalidOperationException("No ceiling types exist in the project.");
    }

    public CeilingType Get() => _defaultType;

    private static CeilingType? FindDefaultCeilingType(Document doc)
    {
        var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.CeilingType);
        if (defaultId != ElementId.InvalidElementId
            && doc.GetElement(defaultId) is CeilingType def)
        {
            return def;
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(CeilingType))
            .Cast<CeilingType>()
            .FirstOrDefault();
    }
}
