using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

/// <summary>
/// Payload materials → Revit materials. The payload's names are already the
/// Revit TEMPLATE vocabulary (the app bridges its canonical keys — see
/// materialBridge.js), so a template that ships "Concrete, Cast-in-Place gray"
/// is REUSED rather than duplicated, and only materials the template lacks are
/// created — with the app's colour, a solid surface/cut fill in that colour,
/// and shading set to colour rather than render appearance so the model reads
/// the same in Revit as it did in BIMy.
/// </summary>
internal static class MaterialFactory
{
    public static void Build(BuildContext ctx)
    {
        var doc = ctx.Doc;
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var solidFill = FindSolidFill(doc);

        foreach (var dto in ctx.Model.Materials)
        {
            try
            {
                var name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name!;
                if (existing.TryGetValue(name, out var reused))
                {
                    ctx.Materials[dto.Id] = reused.Id;
                    continue;
                }

                var id = Material.Create(doc, UniqueName(doc, name));
                var mat = (Material)doc.GetElement(id);
                if (TryParseColor(dto.Color, out var color))
                {
                    mat.Color = color;
                    mat.UseRenderAppearanceForShading = false;
                    if (solidFill is not null)
                    {
                        try
                        {
                            mat.SurfaceForegroundPatternId = solidFill.Id;
                            mat.SurfaceForegroundPatternColor = color;
                            mat.CutForegroundPatternId = solidFill.Id;
                            mat.CutForegroundPatternColor = color;
                        }
                        catch { /* pattern slots vary by category — non-fatal */ }
                    }
                }
                mat.Transparency = Math.Max(0, Math.Min(100, dto.Transparency));
                ParamSet.TrySet(mat, BuiltInParameter.ALL_MODEL_DESCRIPTION, ctx.ImportTag);
                if (!string.IsNullOrWhiteSpace(dto.Category))
                {
                    try { mat.MaterialClass = dto.Category!; } catch { /* non-fatal */ }
                }

                existing[name] = mat;
                ctx.Materials[dto.Id] = id;
                ctx.Count("Materials");
            }
            catch (Exception ex)
            {
                ctx.Warn($"Material '{dto.Id}': {ex.Message}");
            }
        }
    }

    private static FillPatternElement? FindSolidFill(Document doc)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(f =>
            {
                try { return f.GetFillPattern().IsSolidFill; }
                catch { return false; }
            });

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = new Color(190, 190, 190);
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex!.TrimStart('#');
        if (s.Length < 6) return false;
        try
        {
            var r = Convert.ToByte(s[..2], 16);
            var g = Convert.ToByte(s[2..4], 16);
            var b = Convert.ToByte(s[4..6], 16);
            color = new Color(r, g, b);
            return true;
        }
        catch { return false; }
    }

    private static string UniqueName(Document doc, string baseName)
    {
        var names = new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!names.Contains(candidate)) return candidate;
        }
        return $"{baseName} ({Guid.NewGuid():N})";
    }
}
