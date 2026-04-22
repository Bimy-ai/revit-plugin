using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Shared helper for turning mm-scaled polygon rings (outer + holes) into the
/// <see cref="CurveLoop"/> list that <see cref="Floor.Create"/> and
/// <see cref="Ceiling.Create"/> expect. Skips sub-mm edges so we never feed
/// Revit a zero-length line.
/// </summary>
internal static class SlabGeometry
{
    private const double MinEdgeLengthMm = 1.0;

    public static IList<CurveLoop> BuildCurveLoops(double[][] outer, double[][][]? holes)
    {
        var loops = new List<CurveLoop>();
        var outerLoop = TryBuildLoop(outer);
        if (outerLoop is not null) loops.Add(outerLoop);

        if (holes is null) return loops;
        foreach (var hole in holes)
        {
            var holeLoop = TryBuildLoop(hole);
            if (holeLoop is not null) loops.Add(holeLoop);
        }
        return loops;
    }

    private static CurveLoop? TryBuildLoop(double[][] ring)
    {
        if (ring is null || ring.Length < 3) return null;

        var pts = new List<XYZ>(ring.Length);
        foreach (var p in ring)
        {
            if (p is null || p.Length < 2) continue;
            var xyz = new XYZ(MmToFeet(p[0]), MmToFeet(p[1]), 0);
            if (pts.Count > 0 && pts[^1].IsAlmostEqualTo(xyz)) continue;
            pts.Add(xyz);
        }
        if (pts.Count > 1 && pts[0].IsAlmostEqualTo(pts[^1])) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return null;

        var loop = new CurveLoop();
        var minEdgeFeet = MmToFeet(MinEdgeLengthMm);
        var added = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (a.DistanceTo(b) < minEdgeFeet) continue;
            try
            {
                loop.Append(Line.CreateBound(a, b));
                added++;
            }
            catch
            {
                // Degenerate segment — Revit will reject it, so skip and hope
                // the remaining edges still form a closed loop.
            }
        }
        return added >= 3 ? loop : null;
    }

    private static double MmToFeet(double mm)
        => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
