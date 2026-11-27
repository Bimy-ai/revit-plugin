using Autodesk.Revit.DB;
using BimyRevit.Models;

namespace BimyRevit.Services.Import;

/// <summary>
/// The payload's `solidElements` — stairs, ramps, columns, foundations, piles,
/// railings — as DirectShapes of REAL extruded solids (true BReps, not meshes)
/// in their proper categories, so a stair schedules as a stair and a pile as a
/// structural foundation. These are the kinds Revit cannot express natively
/// without loadable families; exact solids in the right category is the honest
/// best next thing, and the geometry is the app's to the millimetre.
/// </summary>
internal static class SolidFactory
{
    public static void Build(BuildContext ctx)
    {
        foreach (var el in ctx.Model.SolidElements)
        {
            try
            {
                var level = ctx.LevelAt(el.Storey);
                var geo = new List<GeometryObject>();
                foreach (var ex in el.Extrusions) AddExtrusion(geo, ex);
                foreach (var cy in el.Cylinders) AddCylinder(geo, cy);
                if (geo.Count == 0)
                {
                    ctx.Warn($"{el.Category} {el.Id}: no usable solids.");
                    continue;
                }

                // Local-frame elements (placed families) rotate about their
                // anchor and translate to it; world-frame solids only lift to
                // their storey.
                var move = Transform.CreateTranslation(new XYZ(0, 0,
                    level.Elevation + ImportGeometry.Ft(el.ZOffsetMm)));
                if (el.At is { Length: >= 2 })
                {
                    var place = Transform.CreateTranslation(
                        new XYZ(ImportGeometry.Ft(el.At[0]), ImportGeometry.Ft(el.At[1]), 0));
                    var spin = Transform.CreateRotation(XYZ.BasisZ, el.RotationDeg * Math.PI / 180);
                    move = move.Multiply(place).Multiply(spin);
                }
                geo = geo.Select(g => g is Solid s ? (GeometryObject)SolidUtils.CreateTransformed(s, move) : g).ToList();

                var ds = DirectShape.CreateElement(ctx.Doc, CategoryFor(ctx.Doc, el.Category));
                ds.SetShape(geo);
                ctx.Brand(ds, el.Guid ?? el.Id, el.Name);
                ctx.Count(LabelFor(el.Category));
            }
            catch (Exception ex)
            {
                ctx.Warn($"{el.Category} {el.Id}: {ex.Message}");
            }
        }
    }

    private static void AddExtrusion(List<GeometryObject> geo, ExtrusionDto ex)
    {
        var dz = ImportGeometry.Ft(Math.Max(5, ex.DzMm));
        var loops = ImportGeometry.Loops(ex.Ring, ex.Holes, ImportGeometry.Ft(ex.Z0Mm));
        if (loops.Count == 0) return;
        geo.Add(GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, dz));
    }

    private static void AddCylinder(List<GeometryObject> geo, CylinderDto cy)
    {
        var loop = ImportGeometry.Circle(cy.Center, cy.RMm, ImportGeometry.Ft(cy.Z0Mm));
        if (loop is null) return;
        geo.Add(GeometryCreationUtilities.CreateExtrusionGeometry(
            new List<CurveLoop> { loop }, XYZ.BasisZ, ImportGeometry.Ft(Math.Max(5, cy.DzMm))));
    }

    /// <summary>The category a kind schedules under; GenericModel when this
    /// Revit refuses DirectShapes in the preferred one.</summary>
    public static ElementId CategoryFor(Document doc, string? kind)
    {
        var preferred = kind switch
        {
            "stair" => BuiltInCategory.OST_Stairs,
            "ramp" => BuiltInCategory.OST_Ramps,
            "column" => BuiltInCategory.OST_Columns,
            "foundation" or "pile" => BuiltInCategory.OST_StructuralFoundation,
            "railing" => BuiltInCategory.OST_StairsRailing,
            "furniture" => BuiltInCategory.OST_Furniture,
            _ => BuiltInCategory.OST_GenericModel,
        };
        var id = new ElementId(preferred);
        try
        {
            if (DirectShape.IsValidCategoryId(id, doc)) return id;
        }
        catch { /* fall through */ }
        return new ElementId(BuiltInCategory.OST_GenericModel);
    }

    private static string LabelFor(string? kind) => kind switch
    {
        "stair" => "Stairs",
        "ramp" => "Ramps",
        "column" => "Columns",
        "pile" => "Piles",
        "foundation" => "Foundations",
        "railing" => "Railings",
        _ => "Generic models",
    };

    /// <summary>
    /// A payload mesh → tessellated geometry, with the material baked onto the
    /// faces (the only way a DirectShape takes one). Used by the roof fallback
    /// and the furniture types.
    /// </summary>
    public static IList<GeometryObject> MeshToShape(
        BuildContext ctx, MeshDto mesh, ElementId materialId, XYZ offset)
    {
        var pts = new List<XYZ>(mesh.Positions.Count / 3);
        for (var i = 0; i + 2 < mesh.Positions.Count; i += 3)
        {
            pts.Add(new XYZ(
                ImportGeometry.Ft(mesh.Positions[i]) + offset.X,
                ImportGeometry.Ft(mesh.Positions[i + 1]) + offset.Y,
                ImportGeometry.Ft(mesh.Positions[i + 2]) + offset.Z));
        }
        return TrianglesToShape(ctx, pts, mesh.Indices, materialId);
    }

    public static IList<GeometryObject> TrianglesToShape(
        BuildContext ctx, IReadOnlyList<XYZ> pts, IReadOnlyList<int> indices, ElementId materialId)
    {
        try
        {
            var builder = new TessellatedShapeBuilder
            {
                Target = TessellatedShapeBuilderTarget.Mesh,
                Fallback = TessellatedShapeBuilderFallback.Salvage,
                GraphicsStyleId = ElementId.InvalidElementId,
            };
            builder.OpenConnectedFaceSet(false);
            var added = 0;
            for (var t = 0; t + 2 < indices.Count; t += 3)
            {
                var a = indices[t];
                var b = indices[t + 1];
                var c = indices[t + 2];
                if (a >= pts.Count || b >= pts.Count || c >= pts.Count) continue;
                var pa = pts[a];
                var pb = pts[b];
                var pc = pts[c];
                // Zero-area triangles make the builder give up on the whole set.
                if (pa.DistanceTo(pb) < 1e-6 || pb.DistanceTo(pc) < 1e-6 || pc.DistanceTo(pa) < 1e-6) continue;
                builder.AddFace(new TessellatedFace(new List<XYZ> { pa, pb, pc }, materialId));
                added++;
            }
            builder.CloseConnectedFaceSet();
            if (added == 0) return new List<GeometryObject>();
            builder.Build();
            return builder.GetBuildResult().GetGeometricalObjects();
        }
        catch (Exception ex)
        {
            ctx.Warn($"Mesh geometry failed: {ex.Message}");
            return new List<GeometryObject>();
        }
    }
}
