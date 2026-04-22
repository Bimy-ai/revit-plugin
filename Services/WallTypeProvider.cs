using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Materialises a Revit <see cref="WallType"/> for each requested color.
/// Uses the project's default Basic Wall as the template and preserves its
/// compound structure (finishes, substrate, thermal layer, etc.) — only the
/// structural layer's material is swapped to one of the requested color.
///
/// The produced walls therefore render exactly like walls drawn with Revit's
/// built-in Walls tool, just in the requested colour.
///
/// Must be used inside an open transaction.
/// </summary>
internal sealed class WallTypeProvider
{
    private readonly Document _doc;
    private readonly WallType _baseType;
    private readonly Dictionary<string, WallType> _wallTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _reservedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ElementId> _materialCache = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CreatedWallTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public WallTypeProvider(Document doc)
    {
        _doc = doc;
        _baseType = FindBaseBasicWallType(doc)
            ?? throw new InvalidOperationException("No basic wall types exist in the project.");
    }

    /// <summary>
    /// Returns the wall type to use for a wall of the given floor-type name and color.
    /// The wall type is displayed in Revit as "RWP &lt;typeName&gt;" (falling back to
    /// "Generic"); when the same typeName is used with multiple colors, later ones are
    /// disambiguated with a hex suffix so each color still gets its own wall type.
    /// When <paramref name="colorHex"/> is null/empty, the base wall type is
    /// returned as-is (no duplication, no project mutation).
    /// </summary>
    public WallType Get(string? typeName, string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
            return _baseType;

        var hex = NormalizeHex(colorHex!);
        var displayBase = string.IsNullOrWhiteSpace(typeName) ? "Generic" : typeName!.Trim();
        var cacheKey = $"{displayBase}|{hex}";

        if (_wallTypeCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var desiredName = $"RWP {displayBase}";
        if (_reservedNames.TryGetValue(desiredName, out var claimedHex)
            && !string.Equals(claimedHex, hex, StringComparison.OrdinalIgnoreCase))
        {
            desiredName = $"RWP {displayBase} {hex}";
        }
        _reservedNames[desiredName] = hex;

        var existing = new FilteredElementCollector(_doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(wt => string.Equals(wt.Name, desiredName, StringComparison.OrdinalIgnoreCase));

        WallType wallType;
        if (existing is not null)
        {
            ReplaceStructuralMaterial(existing, EnsureMaterial(hex));
            wallType = existing;
        }
        else
        {
            wallType = (WallType)_baseType.Duplicate(desiredName);
            ReplaceStructuralMaterial(wallType, EnsureMaterial(hex));
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

    private static void ReplaceStructuralMaterial(WallType wallType, ElementId materialId)
    {
        var cs = wallType.GetCompoundStructure();
        if (cs is null) return;

        var layers = cs.GetLayers();
        var targetIdx = -1;
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].Function == MaterialFunctionAssignment.Structure)
            {
                targetIdx = i;
                break;
            }
        }
        if (targetIdx < 0) targetIdx = 0;

        cs.SetMaterialId(targetIdx, materialId);
        wallType.SetCompoundStructure(cs);
    }

    private ElementId EnsureMaterial(string hex)
    {
        if (_materialCache.TryGetValue(hex, out var cached)) return cached;

        var name = $"RWP {hex}";

        var existing = new FilteredElementCollector(_doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        ElementId id;
        if (existing is not null)
        {
            id = existing.Id;
        }
        else
        {
            id = Material.Create(_doc, name);
            if (_doc.GetElement(id) is Material mat)
            {
                mat.Color = ParseHex(hex);
                mat.UseRenderAppearanceForShading = false;
                mat.Transparency = 0;
            }
        }

        _materialCache[hex] = id;
        return id;
    }

    private static Autodesk.Revit.DB.Color ParseHex(string hex)
    {
        var s = hex.StartsWith("#") ? hex.Substring(1) : hex;
        if (s.Length != 6) return new Autodesk.Revit.DB.Color(160, 160, 160);
        try
        {
            var r = Convert.ToByte(s.Substring(0, 2), 16);
            var g = Convert.ToByte(s.Substring(2, 2), 16);
            var b = Convert.ToByte(s.Substring(4, 2), 16);
            return new Autodesk.Revit.DB.Color(r, g, b);
        }
        catch
        {
            return new Autodesk.Revit.DB.Color(160, 160, 160);
        }
    }

    private static string NormalizeHex(string hex)
    {
        var s = hex.Trim();
        if (!s.StartsWith("#")) s = "#" + s;
        if (s.Length != 7) return "#a0a0a0";
        return s.ToLowerInvariant();
    }
}
