using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Materialises a Revit <see cref="WallType"/> for each (typeId/name, thickness)
/// combination requested by the import. Produces single-layer basic wall types
/// whose one structural layer is the requested thickness + color, so 3D renders,
/// plans, and sections all show the wall with its intended width and surface color.
///
/// When a stable typeId is supplied, every call with that id resolves to the
/// same WallType — this is how walls that share a source type "link to the
/// same" in Revit: edit the type once, every instance updates. Types are named
/// "{typeName} {thicknessMm}mm" so repeated imports reuse the same Revit name.
///
/// Imported types are branded via the Type Comments field with the tag
/// <see cref="ImportedTag"/>, letting downstream code (e.g. DeleteImportedWalls)
/// identify imported assets without relying on name prefixes.
///
/// Must be used inside an open transaction.
/// </summary>
internal sealed class WallTypeProvider
{
    public const string ImportedTag = "BIMy import";

    private readonly Document _doc;
    private readonly ImportMaterialProvider _materials;
    private readonly WallType _baseType;
    private readonly Dictionary<string, WallType> _wallTypeCache = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CreatedWallTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public WallTypeProvider(Document doc, ImportMaterialProvider materials)
    {
        _doc = doc;
        _materials = materials;
        _baseType = FindBaseBasicWallType(doc)
            ?? throw new InvalidOperationException("No basic wall types exist in the project.");

        PrepopulateWallTypeCache();
    }

    public WallType Get(string? typeId, string? typeName, double thicknessMm, string? colorHex)
    {
        var hex = ImportColor.Normalize(colorHex);
        var displayBase = string.IsNullOrWhiteSpace(typeName) ? "Generic" : typeName!.Trim();
        var roundedMm = (int)Math.Round(thicknessMm);
        if (roundedMm <= 0) roundedMm = 200;

        var cacheKey = !string.IsNullOrWhiteSpace(typeId)
            ? $"id:{typeId}|{roundedMm}"
            : $"name:{displayBase}|{roundedMm}";
        if (_wallTypeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var desiredName = $"{displayBase} {roundedMm}mm";

        var existing = new FilteredElementCollector(_doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(wt => string.Equals(wt.Name, desiredName, StringComparison.OrdinalIgnoreCase));

        var materialId = _materials.EnsureMaterial(hex);
        var thicknessFeet = UnitUtils.ConvertToInternalUnits(roundedMm, UnitTypeId.Millimeters);

        WallType wallType;
        if (existing is not null)
        {
            ApplySingleLayer(existing, materialId, thicknessFeet);
            wallType = existing;
        }
        else
        {
            wallType = (WallType)_baseType.Duplicate(desiredName);
            ApplySingleLayer(wallType, materialId, thicknessFeet);
            TagAsImported(wallType);
            CreatedWallTypes.Add(desiredName);
        }

        _wallTypeCache[cacheKey] = wallType;
        return wallType;
    }

    private static WallType? FindBaseBasicWallType(Document doc)
    {
        var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
        if (defaultId != ElementId.InvalidElementId
            && doc.GetElement(defaultId) is WallType def
            && def.Kind == WallKind.Basic)
        {
            return def;
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(wt => wt.Kind == WallKind.Basic);
    }

    private void PrepopulateWallTypeCache()
    {
        // Only name-based cache keys can be inferred from existing types.
        // TypeId-based keys populate lazily on the first Get(typeId, …) call.
        foreach (var wt in new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>())
        {
            if (wt.Kind != WallKind.Basic) continue;
            if (!TryParseImportedName(wt.Name, out var baseName, out var thicknessMm)) continue;
            var key = $"name:{baseName}|{thicknessMm}";
            if (!_wallTypeCache.ContainsKey(key))
                _wallTypeCache[key] = wt;
        }
    }

    private static bool TryParseImportedName(string name, out string baseName, out int thicknessMm)
    {
        baseName = string.Empty;
        thicknessMm = 0;

        if (string.IsNullOrWhiteSpace(name)) return false;
        var parts = name.Split(' ');
        if (parts.Length < 2) return false;

        var last = parts[^1];
        if (!last.EndsWith("mm", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(last[..^2], out thicknessMm)) return false;

        baseName = string.Join(' ', parts, 0, parts.Length - 1);
        return baseName.Length > 0;
    }

    private void ApplySingleLayer(WallType wallType, ElementId materialId, double thicknessFeet)
    {
        var cs = CompoundStructure.CreateSingleLayerCompoundStructure(
            MaterialFunctionAssignment.Structure,
            thicknessFeet,
            materialId);
        wallType.SetCompoundStructure(cs);
    }

    private void TagAsImported(WallType wallType)
    {
        var p = wallType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
        if (p != null && !p.IsReadOnly)
        {
            try { p.Set(ImportedTag); } catch { /* non-fatal */ }
        }
    }
}
