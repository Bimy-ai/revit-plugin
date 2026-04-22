using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Per-(typeId, thickness) cache of Revit <see cref="FloorType"/>s, mirroring
/// <see cref="WallTypeProvider"/>. Floors that share a source type id resolve
/// to the same Revit FloorType, so recolouring the type once recolours every
/// slab instance on every story. Imported types are branded via Type Comments
/// with <see cref="WallTypeProvider.ImportedTag"/>.
/// Must be used inside an open transaction.
/// </summary>
internal sealed class FloorTypeProvider
{
    private readonly Document _doc;
    private readonly ImportMaterialProvider _materials;
    private readonly FloorType _baseType;
    private readonly Dictionary<string, FloorType> _cache = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CreatedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FloorTypeProvider(Document doc, ImportMaterialProvider materials)
    {
        _doc = doc;
        _materials = materials;
        _baseType = FindBaseFloorType(doc)
            ?? throw new InvalidOperationException("No floor types exist in the project.");
    }

    public FloorType Get(string? typeId, string? typeName, double thicknessMm, string? colorHex)
    {
        var hex = ImportColor.Normalize(colorHex);
        var displayBase = string.IsNullOrWhiteSpace(typeName) ? "Generic" : typeName!.Trim();
        var roundedMm = (int)Math.Round(thicknessMm);
        if (roundedMm <= 0) roundedMm = 150;

        var cacheKey = !string.IsNullOrWhiteSpace(typeId)
            ? $"id:{typeId}|{roundedMm}"
            : $"name:{displayBase}|{roundedMm}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var desiredName = $"{displayBase} Floor {roundedMm}mm";

        var existing = new FilteredElementCollector(_doc)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .FirstOrDefault(t => string.Equals(t.Name, desiredName, StringComparison.OrdinalIgnoreCase));

        var materialId = _materials.EnsureMaterial(hex);
        var thicknessFeet = UnitUtils.ConvertToInternalUnits(roundedMm, UnitTypeId.Millimeters);

        FloorType floorType;
        if (existing is not null)
        {
            TryApplySingleLayer(existing, materialId, thicknessFeet);
            floorType = existing;
        }
        else
        {
            floorType = (FloorType)_baseType.Duplicate(desiredName);
            TryApplySingleLayer(floorType, materialId, thicknessFeet);
            TagAsImported(floorType);
            CreatedTypes.Add(desiredName);
        }

        _cache[cacheKey] = floorType;
        return floorType;
    }

    private static FloorType? FindBaseFloorType(Document doc)
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

    private static void TryApplySingleLayer(FloorType floorType, ElementId materialId, double thicknessFeet)
    {
        try
        {
            var cs = CompoundStructure.CreateSingleLayerCompoundStructure(
                MaterialFunctionAssignment.Structure,
                thicknessFeet,
                materialId);
            floorType.SetCompoundStructure(cs);
        }
        catch
        {
            // Some floor-type families reject SetCompoundStructure; leave the
            // duplicated type as-is so the import still produces a slab.
        }
    }

    private static void TagAsImported(FloorType floorType)
    {
        var p = floorType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
        if (p != null && !p.IsReadOnly)
        {
            try { p.Set(WallTypeProvider.ImportedTag); } catch { /* non-fatal */ }
        }
    }
}
