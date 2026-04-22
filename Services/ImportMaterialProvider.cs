using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Central cache/factory for the "BIMy {hex}" materials that walls, floors,
/// and ceilings all share. Every importable color resolves to exactly one
/// Revit <see cref="Material"/>, so two elements of the same color display
/// consistently and a single recolor touches every instance at once.
/// Must be used inside an open transaction.
/// </summary>
internal sealed class ImportMaterialProvider
{
    public const string ImportedTag = WallTypeProvider.ImportedTag;

    private readonly Document _doc;
    private readonly Dictionary<string, ElementId> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ElementId _solidFillPatternId;

    public ImportMaterialProvider(Document doc)
    {
        _doc = doc;
        _solidFillPatternId = FindSolidFillPatternId(doc);
        PrepopulateCache();
    }

    public ElementId EnsureMaterial(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return ElementId.InvalidElementId;
        var normalised = hex!.ToLowerInvariant();
        if (_cache.TryGetValue(normalised, out var cached)) return cached;

        var name = $"BIMy {normalised}";

        var existing = new FilteredElementCollector(_doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        ElementId id;
        if (existing is not null)
        {
            id = existing.Id;
            Paint(existing, normalised);
        }
        else
        {
            id = Material.Create(_doc, name);
            if (_doc.GetElement(id) is Material mat) Paint(mat, normalised);
        }

        _cache[normalised] = id;
        return id;
    }

    private void PrepopulateCache()
    {
        foreach (var m in new FilteredElementCollector(_doc).OfClass(typeof(Material)).Cast<Material>())
        {
            var hex = ImportColor.FromColor(m.Color);
            if (hex is null) continue;
            if (!m.Name.StartsWith("BIMy ", StringComparison.OrdinalIgnoreCase)
                && !m.Name.StartsWith("RWP ", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_cache.ContainsKey(hex)) _cache[hex] = m.Id;
        }
    }

    private void Paint(Material mat, string hex)
    {
        var color = ImportColor.ToRevit(hex);
        mat.Color = color;
        mat.UseRenderAppearanceForShading = false;
        mat.Transparency = 0;

        // Match surface + cut patterns so plan/section views render the colour,
        // not just shaded 3D views.
        if (_solidFillPatternId != ElementId.InvalidElementId)
        {
            try { mat.SurfaceForegroundPatternId = _solidFillPatternId; } catch { }
            try { mat.CutForegroundPatternId = _solidFillPatternId; } catch { }
        }
        try { mat.SurfaceForegroundPatternColor = color; } catch { }
        try { mat.CutForegroundPatternColor = color; } catch { }

        var desc = mat.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);
        if (desc != null && !desc.IsReadOnly)
        {
            try { desc.Set(ImportedTag); } catch { }
        }
    }

    private static ElementId FindSolidFillPatternId(Document doc)
    {
        var pattern = new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
        return pattern?.Id ?? ElementId.InvalidElementId;
    }
}
