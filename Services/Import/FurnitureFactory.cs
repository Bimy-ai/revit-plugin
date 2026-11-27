using Autodesk.Revit.DB;
using BimyRevit.Models;

namespace BimyRevit.Services.Import;

/// <summary>
/// Placed catalog objects (furniture, fixtures, trees, …): ONE DirectShapeType
/// per distinct catalog kind carrying the app's real mesh — each part coloured
/// by its own generated material — and one instanced DirectShape per placement,
/// so forty chairs share one geometry definition and schedule as forty
/// instances of one type. The payload hands the placement frame as two unit
/// vectors, so a prop resting on a ramp leans exactly as the app drew it.
/// </summary>
internal static class FurnitureFactory
{
    public static void Build(BuildContext ctx)
    {
        if (ctx.Model.Furniture.Count == 0) return;
        var doc = ctx.Doc;
        var library = DirectShapeLibrary.GetDirectShapeLibrary(doc);
        var types = new Dictionary<string, (ElementId TypeId, ElementId CategoryId, string DefId)>(StringComparer.Ordinal);

        foreach (var t in ctx.Model.FurnitureTypes)
        {
            try
            {
                var categoryId = SolidFactory.CategoryFor(doc, string.IsNullOrWhiteSpace(t.Category) ? "furniture" : t.Category);
                var geo = BuildTypeGeometry(ctx, t);
                if (geo.Count == 0)
                {
                    ctx.Warn($"Object '{t.Key}': no geometry — instances get a nominal box.");
                    continue;
                }
                var dsType = DirectShapeType.Create(doc, UniqueTypeName(doc, t.Name ?? t.Key), categoryId);
                dsType.SetShape(geo);
                var defId = $"bimy:{t.Key}";
                library.AddDefinitionType(defId, dsType.Id);
                types[t.Key] = (dsType.Id, categoryId, defId);
            }
            catch (Exception ex)
            {
                ctx.Warn($"Object '{t.Key}': {ex.Message}");
            }
        }

        foreach (var f in ctx.Model.Furniture)
        {
            try
            {
                var level = ctx.LevelAt(f.Storey);
                var origin = new XYZ(
                    ImportGeometry.Ft(f.At[0]),
                    ImportGeometry.Ft(f.At[1]),
                    level.Elevation + ImportGeometry.Ft(f.ZOffsetMm));
                var frame = FrameOf(f, origin);

                if (types.TryGetValue(f.TypeKey, out var type))
                {
                    var instance = DirectShape.CreateElementInstance(
                        doc, type.TypeId, type.CategoryId, type.DefId, frame);
                    ctx.Brand(instance, f.Guid ?? f.Id, f.Name);
                }
                else
                {
                    // No mesh came across for this kind — a nominal box in the
                    // right place keeps the object present and selectable.
                    var box = NominalBox(ctx, f);
                    if (box.Count == 0) continue;
                    var ds = DirectShape.CreateElement(doc, SolidFactory.CategoryFor(doc, "furniture"));
                    ds.SetShape(box.Select(g => g is Solid s
                        ? (GeometryObject)SolidUtils.CreateTransformed(s, frame)
                        : g).ToList());
                    ctx.Brand(ds, f.Guid ?? f.Id, f.Name);
                }
                ctx.Count("Objects");
            }
            catch (Exception ex)
            {
                ctx.Warn($"Object {f.Id}: {ex.Message}");
            }
        }
    }

    private static IList<GeometryObject> BuildTypeGeometry(BuildContext ctx, FurnitureTypeDto t)
    {
        var geo = new List<GeometryObject>();
        foreach (var part in t.Parts)
        {
            if (part.Positions.Count < 9 || part.Indices.Count < 3) continue;
            var material = PartMaterial(ctx, part);
            var pts = new List<XYZ>(part.Positions.Count / 3);
            for (var i = 0; i + 2 < part.Positions.Count; i += 3)
            {
                pts.Add(new XYZ(
                    ImportGeometry.Ft(part.Positions[i]),
                    ImportGeometry.Ft(part.Positions[i + 1]),
                    ImportGeometry.Ft(part.Positions[i + 2])));
            }
            foreach (var g in SolidFactory.TrianglesToShape(ctx, pts, part.Indices, material))
                geo.Add(g);
        }
        if (geo.Count == 0 && t.Box is { WMm: > 0, DMm: > 0, HMm: > 0 })
        {
            var loop = ImportGeometry.Loop(new List<double[]>
            {
                new[] { -t.Box.WMm / 2, -t.Box.DMm / 2 },
                new[] { t.Box.WMm / 2, -t.Box.DMm / 2 },
                new[] { t.Box.WMm / 2, t.Box.DMm / 2 },
                new[] { -t.Box.WMm / 2, t.Box.DMm / 2 },
            }, 0);
            if (loop is not null)
                geo.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, XYZ.BasisZ, ImportGeometry.Ft(t.Box.HMm)));
        }
        return geo;
    }

    /// <summary>One "BIMy Object #rrggbb" material per distinct part colour,
    /// shared across every object that wears it.</summary>
    private static ElementId PartMaterial(BuildContext ctx, FurnitureMeshPartDto part)
    {
        var color = string.IsNullOrWhiteSpace(part.Color) ? "#cccccc" : part.Color!;
        var key = $"@object:{color}:{part.Opacity:0.##}";
        if (ctx.Materials.TryGetValue(key, out var cached)) return cached;
        try
        {
            var existing = new FilteredElementCollector(ctx.Doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Name.Equals($"BIMy Object {color}", StringComparison.OrdinalIgnoreCase));
            var id = existing?.Id;
            if (id is null)
            {
                id = Material.Create(ctx.Doc, $"BIMy Object {color}");
                var mat = (Material)ctx.Doc.GetElement(id);
                var s = color.TrimStart('#');
                mat.Color = new Color(
                    Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
                mat.UseRenderAppearanceForShading = false;
                mat.Transparency = (int)Math.Round((1 - Math.Clamp(part.Opacity, 0, 1)) * 100);
            }
            ctx.Materials[key] = id;
            return id;
        }
        catch
        {
            return ElementId.InvalidElementId;
        }
    }

    private static IList<GeometryObject> NominalBox(BuildContext ctx, FurnitureDto f)
    {
        _ = f;
        var loop = ImportGeometry.Loop(new List<double[]>
        {
            new[] { -250.0, -250.0 },
            new[] { 250.0, -250.0 },
            new[] { 250.0, 250.0 },
            new[] { -250.0, 250.0 },
        }, 0);
        if (loop is null) return new List<GeometryObject>();
        _ = ctx;
        return new List<GeometryObject>
        {
            GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop }, XYZ.BasisZ, ImportGeometry.Ft(500)),
        };
    }

    /// <summary>The instance transform: BasisX from `right`, BasisZ from `up`,
    /// BasisY their cross — uniform scale folded into the bases.</summary>
    private static Transform FrameOf(FurnitureDto f, XYZ origin)
    {
        var up = Vec(f.Up, XYZ.BasisZ);
        var right = Vec(f.Right, XYZ.BasisX);
        // Re-orthogonalise: the payload rounds to 3 decimals, and Revit wants a
        // clean frame.
        var y = up.CrossProduct(right);
        if (y.GetLength() < 1e-6) { right = XYZ.BasisX; y = up.CrossProduct(right); }
        y = y.Normalize();
        right = y.CrossProduct(up).Normalize();

        var t = Transform.Identity;
        var s = Math.Max(1e-3, f.Scale);
        t.Origin = origin;
        t.BasisX = right * s;
        t.BasisY = y * s;
        t.BasisZ = up.Normalize() * s;
        return t;
    }

    private static XYZ Vec(double[] v, XYZ fallback)
    {
        if (v is not { Length: >= 3 }) return fallback;
        var x = new XYZ(v[0], v[1], v[2]);
        return x.GetLength() < 1e-6 ? fallback : x.Normalize();
    }

    private static string UniqueTypeName(Document doc, string baseName)
    {
        var names = new FilteredElementCollector(doc)
            .OfClass(typeof(DirectShapeType))
            .Select(t => t.Name)
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
