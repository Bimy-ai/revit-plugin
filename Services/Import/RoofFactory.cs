using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

/// <summary>
/// Roofs. Two paths, picked by the payload's own `nativeEligible` verdict:
///
/// NATIVE — a real FootPrintRoof from the overhang-grown footprint, with
/// `DefinesSlope` + the pitch on exactly the edges the app says are eaves.
/// Revit's own straight skeleton then rebuilds the gable/hip/shed the app
/// rendered, as a roof the user can re-pitch and re-edge natively.
///
/// MESH — the exact triangles the app renders (courtyard roofs, capped
/// gables, shed `start` offsets — shapes a footprint roof cannot state),
/// as a DirectShape in the Roofs category. Faithful, selectable, printable;
/// not parametrically editable, which the summary says honestly.
/// </summary>
internal static class RoofFactory
{
    public static void Build(BuildContext ctx)
    {
        foreach (var roof in ctx.Model.Roofs)
        {
            try
            {
                if (roof.NativeEligible && roof.SlopedEdges is { Count: > 0 } && TryNative(ctx, roof))
                    continue;
                if (roof.Mesh is { Positions.Count: >= 9 })
                {
                    BuildMesh(ctx, roof);
                    continue;
                }
                // No mesh and not native-eligible: the flat footprint is still
                // a better answer than nothing.
                if (!TryNative(ctx, roof, forceFlat: true))
                    ctx.Warn($"Roof {roof.Id}: no buildable representation.");
            }
            catch (Exception ex)
            {
                ctx.Warn($"Roof {roof.Id}: {ex.Message}");
            }
        }
    }

    private static bool TryNative(BuildContext ctx, Models.RoofDto dto, bool forceFlat = false)
    {
        var doc = ctx.Doc;
        var level = ctx.LevelAt(dto.Storey);
        var type = TypeFactory.RoofTypeFor(ctx, dto);

        // NewFootPrintRoof wants a CurveArray; holes ride along as inner loops.
        var loops = ImportGeometry.Loops(dto.Ring, dto.Holes, level.Elevation);
        if (loops.Count == 0) return false;
        var footprint = new CurveArray();
        foreach (var loop in loops)
            foreach (var curve in loop)
                footprint.Append(curve);

        FootPrintRoof roof;
        ModelCurveArray mapping;
        try
        {
            roof = doc.Create.NewFootPrintRoof(footprint, level, type, out mapping);
        }
        catch (Exception ex)
        {
            ctx.Warn($"Roof {dto.Id}: footprint rejected ({ex.Message}).");
            return false;
        }

        // Slopes on the eave edges the app computed. The mapping returns one
        // model curve per appended footprint curve, in order; the outer ring's
        // edges come first because its loop was appended first.
        if (!forceFlat && dto.SlopedEdges is { Count: > 0 })
        {
            var slope = Math.Tan(Math.Max(0, dto.PitchDeg) * Math.PI / 180);
            var outerCount = loops[0].Count();
            var i = 0;
            foreach (ModelCurve mc in mapping)
            {
                var slopes = i < outerCount
                             && i < dto.SlopedEdges.Count
                             && dto.SlopedEdges[i]
                             && slope > 1e-6;
                try
                {
                    roof.set_DefinesSlope(mc, slopes);
                    if (slopes) roof.set_SlopeAngle(mc, slope);
                }
                catch { /* a degenerate edge refuses — the rest still slope */ }
                i++;
            }
        }
        else
        {
            foreach (ModelCurve mc in mapping)
            {
                try { roof.set_DefinesSlope(mc, false); } catch { /* non-fatal */ }
            }
        }

        ParamSet.TrySet(roof, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM, ImportGeometry.Ft(dto.ZOffsetMm));
        ctx.Brand(roof, dto.Guid ?? dto.Id);
        ctx.Count("Roofs");
        return true;
    }

    private static void BuildMesh(BuildContext ctx, Models.RoofDto dto)
    {
        var level = ctx.LevelAt(dto.Storey);
        // Mesh coordinates are storey-frame with z = 0 at the eave plane.
        var lift = level.Elevation + ImportGeometry.Ft(dto.ZOffsetMm);
        var shape = SolidFactory.MeshToShape(
            ctx, dto.Mesh!, ctx.MaterialId(dto.MaterialId), new XYZ(0, 0, lift));
        if (shape.Count == 0)
        {
            ctx.Warn($"Roof {dto.Id}: mesh produced no geometry.");
            return;
        }
        var ds = DirectShape.CreateElement(ctx.Doc, new ElementId(BuiltInCategory.OST_Roofs));
        ds.SetShape(shape);
        ctx.Brand(ds, dto.Guid ?? dto.Id, dto.Name ?? "Roof");
        ctx.Count("Roofs");
    }
}
