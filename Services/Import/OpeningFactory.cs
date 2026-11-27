using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace BimyRevit.Services.Import;

/// <summary>
/// Doors, windows and plain openings, hosted on their payload walls.
///
/// Placement is BY WORLD POINT: the payload carries each opening's centre, the
/// family instance is created at that point on its host, and Revit projects it
/// onto the wall — which makes a door in an arc wall exactly as easy as one in
/// a straight wall. The wall cuts itself around the hosted instance; `unfilled`
/// records (a hole, nothing built in it) and machines with no door/window
/// family fall back to a real rectangular wall opening instead, so geometry
/// never silently diverges from the app.
/// </summary>
internal static class OpeningFactory
{
    public static void Build(BuildContext ctx)
    {
        foreach (var op in ctx.Model.Openings)
        {
            try
            {
                if (!ctx.WallsById.TryGetValue(op.HostWallId, out var host))
                {
                    ctx.Warn($"Opening {op.Id}: host wall {op.HostWallId} was not built — skipped.");
                    continue;
                }
                var level = ctx.LevelAt(op.Storey);

                var isPlainHole = op.Unfilled || op.Kind == "opening";
                var symbol = isPlainHole ? null : TypeFactory.OpeningSymbolFor(ctx, op);
                if (symbol is null)
                {
                    CutOpening(ctx, host, op, level, countAs: isPlainHole ? "Openings" : (op.Kind == "window" ? "Windows" : "Doors"));
                    continue;
                }

                // The sill in the payload is measured from the WALL's base (the
                // storey base plus the wall's own base offset carries it).
                var baseOffset = host.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
                var zSill = level.Elevation + baseOffset + ImportGeometry.Ft(op.SillMm);
                var at = ImportGeometry.Pt(op.Center, zSill);

                var instance = ctx.Doc.Create.NewFamilyInstance(
                    at, symbol, host, level, StructuralType.NonStructural);

                // Windows hang off their sill-height instance parameter; doors
                // sit on the wall base by construction.
                if (op.Kind == "window" || op.SillMm > 0)
                    ParamSet.TrySet(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM,
                        baseOffset + ImportGeometry.Ft(op.SillMm));

                try
                {
                    if (op.FlipHand) instance.flipHand();
                    if (op.FlipFacing) instance.flipFacing();
                }
                catch { /* some families refuse a flip — non-fatal */ }

                ctx.Brand(instance, op.Guid ?? op.Id);
                if (!string.IsNullOrWhiteSpace(op.Name))
                    ParamSet.TrySet(instance, BuiltInParameter.ALL_MODEL_MARK, op.Name!);
                ctx.Count(op.Kind == "window" ? "Windows" : "Doors");
            }
            catch (Exception ex)
            {
                ctx.Warn($"Opening {op.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>A rectangular wall opening — the honest fallback that keeps the
    /// void exactly where and how big the app says.</summary>
    private static void CutOpening(BuildContext ctx, Wall host, Models.OpeningDto op, Level level, string countAs)
    {
        try
        {
            if (host.Location is not LocationCurve lc)
            {
                ctx.Warn($"Opening {op.Id}: host has no location curve.");
                return;
            }
            var curve = lc.Curve;
            var center = ImportGeometry.Pt(op.Center, level.Elevation);
            var at = curve.Project(center);
            if (at is null)
            {
                ctx.Warn($"Opening {op.Id}: centre does not project onto its wall.");
                return;
            }
            // Corners measured along the wall from the projected centre. For an
            // arc wall NewOpening still wants two points — Revit measures the
            // cut along the curve between them.
            var halfW = ImportGeometry.Ft(op.WidthMm) / 2;
            var p0 = PointAlong(curve, at.Parameter, -halfW);
            var p1 = PointAlong(curve, at.Parameter, +halfW);
            var baseOffset = host.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
            var z0 = level.Elevation + baseOffset + ImportGeometry.Ft(op.SillMm);
            var z1 = z0 + ImportGeometry.Ft(Math.Max(50, op.HeightMm));

            var opening = ctx.Doc.Create.NewOpening(
                host,
                new XYZ(p0.X, p0.Y, z0),
                new XYZ(p1.X, p1.Y, z1));
            ctx.Brand(opening, op.Guid ?? op.Id);
            ctx.Count(countAs == "Openings" ? "Openings" : countAs + " (as openings)");
        }
        catch (Exception ex)
        {
            ctx.Warn($"Opening {op.Id}: {ex.Message}");
        }
    }

    private static XYZ PointAlong(Curve curve, double fromParameter, double distanceFt)
    {
        try
        {
            // Normalized-parameter walk: robust for lines and arcs alike.
            var p0 = curve.ComputeNormalizedParameter(fromParameter);
            var length = Math.Max(curve.Length, 1e-6);
            var p = Math.Max(0.0, Math.Min(1.0, p0 + distanceFt / length));
            return curve.Evaluate(p, normalized: true);
        }
        catch
        {
            return curve.Evaluate(0.5, normalized: true);
        }
    }
}
