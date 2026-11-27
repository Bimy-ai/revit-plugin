using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace BimyRevit.Services.Import;

/// <summary>
/// Payload walls → native Revit walls. The payload curve is the wall's BUILT
/// CENTRELINE (the app already applied the drawn reference line), arcs are true
/// arcs, and neighbouring walls share endpoints — so Revit's own joins mitre
/// the corners exactly as a wall-by-wall author would get. The drawn reference
/// line survives as the Location Line parameter (set after creation, which
/// re-anchors without moving the body), so dragging a wall in Revit behaves
/// like dragging it in BIMy.
/// </summary>
internal static class WallFactory
{
    public static void Build(BuildContext ctx)
    {
        var doc = ctx.Doc;
        var levelsByElevation = ctx.Levels.Values
            .Concat(ctx.TopLevel is null ? Enumerable.Empty<Level>() : new[] { ctx.TopLevel })
            .OrderBy(l => l.Elevation)
            .ToList();

        foreach (var w in ctx.Model.Walls)
        {
            try
            {
                var level = ctx.LevelAt(w.Storey);
                var curve = ImportGeometry.WallCurve(w, level.Elevation);
                if (curve is null)
                {
                    ctx.Warn($"Wall {w.Id}: degenerate curve — skipped.");
                    continue;
                }

                var type = TypeFactory.WallTypeFor(ctx, w);
                var heightFt = ImportGeometry.Ft(Math.Max(100, w.HeightMm));
                var baseOffsetFt = ImportGeometry.Ft(w.BaseOffsetMm);

                var wall = Wall.Create(
                    doc, curve, type.Id, level.Id,
                    heightFt, baseOffsetFt,
                    flip: false,
                    structural: w.Kind == "structural");

                // The drawn reference line. The payload's editor normal (right
                // of direction) is Revit's interior side under flip:false, so
                // +1 (body right of the drawn line) anchors the exterior face.
                var locationLine = w.Baseline switch
                {
                    > 0 => WallLocationLine.FinishFaceExterior,
                    < 0 => WallLocationLine.FinishFaceInterior,
                    _ => WallLocationLine.WallCenterline,
                };
                ParamSet.TrySet(wall, BuiltInParameter.WALL_KEY_REF_PARAM, (int)locationLine);

                if (w.Kind == "structural")
                    ParamSet.TrySet(wall, BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM, (int)StructuralWallUsage.Bearing);
                else if (w.Kind == "partition")
                    ParamSet.TrySet(wall, BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM, (int)StructuralWallUsage.NonBearing);

                // A wall whose top lands on a level gets constrained to it —
                // the native behaviour (edit the level, the walls follow).
                // Anything else keeps its exact unconnected height.
                var topElev = level.Elevation + baseOffsetFt + heightFt;
                var top = ctx.LevelAtElevation(topElev)
                    ?? levelsByElevation.FirstOrDefault(l => Math.Abs(l.Elevation - topElev) <= 0.0165);
                if (top is not null && top.Id != level.Id)
                {
                    ParamSet.TrySet(wall, BuiltInParameter.WALL_HEIGHT_TYPE, top.Id);
                    ParamSet.TrySet(wall, BuiltInParameter.WALL_TOP_OFFSET, topElev - top.Elevation);
                }

                ctx.Brand(wall, w.Guid ?? w.Id);
                if (!string.IsNullOrWhiteSpace(w.Name))
                    ParamSet.TrySet(wall, BuiltInParameter.ALL_MODEL_MARK, w.Name!);

                ctx.WallsById[w.Id] = wall;
                ctx.Count("Walls");
            }
            catch (Exception ex)
            {
                ctx.Warn($"Wall {w.Id}: {ex.Message}");
            }
        }
    }
}
