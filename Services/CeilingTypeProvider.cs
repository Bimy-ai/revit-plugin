using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Per-(typeId, thickness) cache of Revit <see cref="CeilingType"/>s, mirroring
/// <see cref="FloorTypeProvider"/>. Ceilings sharing a source type id resolve
/// to the same CeilingType, so recolouring or retyping once touches every
/// ceiling instance on every story.
/// Must be used inside an open transaction.
/// </summary>
internal sealed class CeilingTypeProvider
{
    private readonly Document _doc;
    private readonly ImportMaterialProvider _materials;
    private readonly CeilingType _baseType;
    private readonly Dictionary<string, CeilingType> _cache = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CreatedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CeilingTypeProvider(Document doc, ImportMaterialProvider materials)
    {
        _doc = doc;
        _materials = materials;
        _baseType = FindBaseCeilingType(doc)
            ?? throw new InvalidOperationException("No ceiling types exist in the project.");
    }

    public CeilingType Get(string? typeId, string? typeName, double thicknessMm, string? colorHex)
    {
        var hex = ImportColor.Normalize(colorHex);
        var displayBase = string.IsNullOrWhiteSpace(typeName) ? "Generic" : typeName!.Trim();
        var roundedMm = (int)Math.Round(thicknessMm);
        if (roundedMm <= 0) roundedMm = 150;

        var cacheKey = !string.IsNullOrWhiteSpace(typeId)
            ? $"id:{typeId}|{roundedMm}"
            : $"name:{displayBase}|{roundedMm}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var desiredName = $"{displayBase} Ceiling {roundedMm}mm";

        var existing = new FilteredElementCollector(_doc)
            .OfClass(typeof(CeilingType))
            .Cast<CeilingType>()
            .FirstOrDefault(t => string.Equals(t.Name, desiredName, StringComparison.OrdinalIgnoreCase));

        var materialId = _materials.EnsureMaterial(hex);
        var thicknessFeet = UnitUtils.ConvertToInternalUnits(roundedMm, UnitTypeId.Millimeters);

        CeilingType ceilingType;
        if (existing is not null)
        {
            TryApplySingleLayer(existing, materialId, thicknessFeet);
            ceilingType = existing;
        }
        else
        {
            ceilingType = (CeilingType)_baseType.Duplicate(desiredName);
            TryApplySingleLayer(ceilingType, materialId, thicknessFeet);
            TagAsImported(ceilingType);
            CreatedTypes.Add(desiredName);
        }

        _cache[cacheKey] = ceilingType;
        return ceilingType;
    }

    private static CeilingType? FindBaseCeilingType(Document doc)
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

    private static void TryApplySingleLayer(CeilingType ceilingType, ElementId materialId, double thicknessFeet)
    {
        try
        {
            var cs = CompoundStructure.CreateSingleLayerCompoundStructure(
                MaterialFunctionAssignment.Structure,
                thicknessFeet,
                materialId);
            ceilingType.SetCompoundStructure(cs);
        }
        catch
        {
            // Some ceiling families (notably hosted ones) reject
            // SetCompoundStructure; leave the duplicate as-is.
        }
    }

    private static void TagAsImported(CeilingType ceilingType)
    {
        var p = ceilingType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
        if (p != null && !p.IsReadOnly)
        {
            try { p.Set(WallTypeProvider.ImportedTag); } catch { /* non-fatal */ }
        }
    }
}
