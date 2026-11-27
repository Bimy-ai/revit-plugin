using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

/// <summary>
/// Unit and geometry converters for the `bimy-revit-model/1` payload. The
/// payload speaks integer millimetres in Revit's own plan frame (+x east,
/// +y north, z up), so conversion is unit-only — no axis gymnastics here.
/// </summary>
internal static class ImportGeometry
{
    /// <summary>Sub-millimetre edges are measurement noise; Revit rejects
    /// curves shorter than its ~1/256 ft tolerance anyway.</summary>
    public const double MinEdgeFt = 0.0035;

    public static double Ft(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    public static XYZ Pt(double[] xyMm, double zFt = 0)
        => new(Ft(xyMm[0]), Ft(xyMm[1]), zFt);

    /// <summary>
    /// A closed, planar CurveLoop from a payload ring at elevation
    /// <paramref name="zFt"/> — consecutive duplicates merged, sub-mm edges
    /// dropped, winding normalized to <paramref name="counterClockwise"/>
    /// (outer loops CCW, holes CW keeps every Revit consumer happy).
    /// Null when fewer than three usable points survive.
    /// </summary>
    public static CurveLoop? Loop(IReadOnlyList<double[]> ringMm, double zFt, bool counterClockwise = true)
    {
        if (ringMm is null || ringMm.Count < 3) return null;

        var pts = new List<XYZ>(ringMm.Count);
        foreach (var p in ringMm)
        {
            if (p is null || p.Length < 2) continue;
            var xyz = Pt(p, zFt);
            if (pts.Count > 0 && pts[^1].DistanceTo(xyz) < MinEdgeFt) continue;
            pts.Add(xyz);
        }
        if (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) < MinEdgeFt) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return null;

        if (SignedArea(pts) < 0 == counterClockwise) pts.Reverse();

        var loop = new CurveLoop();
        var added = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (a.DistanceTo(b) < MinEdgeFt) continue;
            try
            {
                loop.Append(Line.CreateBound(a, b));
                added++;
            }
            catch
            {
                // Degenerate segment — skip and hope the rest still closes.
            }
        }
        return added >= 3 ? loop : null;
    }

    /// <summary>Outer ring + holes → the loop list Floor/Ceiling.Create and
    /// CreateExtrusionGeometry expect. Empty when the outer ring is unusable.</summary>
    public static IList<CurveLoop> Loops(
        IReadOnlyList<double[]> outerMm,
        IEnumerable<IReadOnlyList<double[]>>? holesMm,
        double zFt)
    {
        var loops = new List<CurveLoop>();
        var outer = Loop(outerMm, zFt, counterClockwise: true);
        if (outer is null) return loops;
        loops.Add(outer);
        foreach (var hole in holesMm ?? Enumerable.Empty<IReadOnlyList<double[]>>())
        {
            var inner = Loop(hole, zFt, counterClockwise: false);
            if (inner is not null) loops.Add(inner);
        }
        return loops;
    }

    /// <summary>A full-circle loop (two semicircular arcs — Revit refuses a
    /// single 360° arc in a CurveLoop).</summary>
    public static CurveLoop? Circle(double[] centerMm, double rMm, double zFt)
    {
        var r = Ft(rMm);
        if (r < MinEdgeFt) return null;
        var c = Pt(centerMm, zFt);
        var p0 = c + new XYZ(r, 0, 0);
        var p1 = c - new XYZ(r, 0, 0);
        var top = c + new XYZ(0, r, 0);
        var bottom = c - new XYZ(0, r, 0);
        var loop = new CurveLoop();
        loop.Append(Arc.Create(p0, p1, top));
        loop.Append(Arc.Create(p1, p0, bottom));
        return loop;
    }

    /// <summary>
    /// The wall's location curve: the payload's true arc when it carries one
    /// (start → end the shorter way round the stored centre, honouring `ccw`),
    /// else the straight line. Null when the geometry is degenerate.
    /// </summary>
    public static Curve? WallCurve(Models.WallDto w, double zFt)
    {
        var a = Pt(w.A, zFt);
        var b = Pt(w.B, zFt);
        if (w.Arc is null)
            return a.DistanceTo(b) < MinEdgeFt ? null : Line.CreateBound(a, b);

        var c = Pt(w.Arc.Center, zFt);
        var r = Ft(w.Arc.RMm);
        if (r < MinEdgeFt) return null;
        var a0 = Math.Atan2(a.Y - c.Y, a.X - c.X);
        var a1 = Math.Atan2(b.Y - c.Y, b.X - c.X);
        var sweep = a1 - a0;
        // Normalise the sweep into the direction the payload states.
        if (w.Arc.Ccw && sweep <= 0) sweep += 2 * Math.PI;
        if (!w.Arc.Ccw && sweep >= 0) sweep -= 2 * Math.PI;
        if (Math.Abs(sweep) < 1e-4 || Math.Abs(sweep) > 2 * Math.PI - 1e-4) return null;
        var mid = a0 + sweep / 2;
        var pMid = new XYZ(c.X + r * Math.Cos(mid), c.Y + r * Math.Sin(mid), zFt);
        try { return Arc.Create(a, b, pMid); }
        catch { return a.DistanceTo(b) < MinEdgeFt ? null : Line.CreateBound(a, b); }
    }

    public static double SignedArea(IReadOnlyList<XYZ> pts)
    {
        double sum = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }
}
