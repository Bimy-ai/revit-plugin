using System.Text.Json;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Converts the raw API payload into flat lists of centered, millimetre-scaled
/// <see cref="WallDto"/>, <see cref="FloorDto"/>, and <see cref="CeilingDto"/>s
/// ready for Revit element creation.
///
/// <para>
/// Walls, floors, and ceilings are all emitted per story — one Revit element
/// per (story × edge or polygon). Stacked stories of the same type share a
/// Revit wall / floor / ceiling type, so editing the type updates every
/// instance, but each story keeps its own instance for per-level editing.
/// </para>
/// <para>
/// Normalizes polygon winding (walls: outer CW, holes CCW to land the exterior
/// face on the correct side with flip=false), merges collinear points, drops
/// sub-mm edges, and dedupes edges shared between polygons on the same
/// (baseLevel, topLevel, type, thickness, color). Produces an elevation map
/// that includes one extra "roof" level above the top story so the top story's
/// walls always have a valid top constraint.
/// </para>
/// </summary>
internal static class ProjectBuilder
{
    public sealed record Result(
        List<WallDto> Walls,
        List<FloorDto> Floors,
        List<CeilingDto> Ceilings,
        Dictionary<string, double> LevelElevationsMm,
        BuildStats Stats);

    /// <summary>
    /// Per-build counters surfaced to the import log. Helps diagnose "walls
    /// missing" reports by recording which input shape was seen and how many
    /// segments were dropped (sub-mm / malformed) before reaching Revit.
    /// </summary>
    public sealed class BuildStats
    {
        public int SegmentObjects { get; set; }
        public int PolygonObjects { get; set; }
        public int SegmentsRead { get; set; }
        public int SegmentsDroppedShort { get; set; }
        public int SegmentsDroppedMalformed { get; set; }
        public int EdgesDeduped { get; set; }
    }

    private const double MinEdgeLengthMm = 1.0;
    private const double CollinearToleranceMm2 = 1.0;
    private const double DefaultStoryHeightMm = 3000;
    private const double DefaultSlabThicknessMm = 150;

    public static Result Build(UserObjectsPayload payload)
    {
        var walls = new List<WallDto>();
        var floors = new List<FloorDto>();
        var ceilings = new List<CeilingDto>();
        var stats = new BuildStats();

        var userObjects = payload.UserObjects ?? new List<UserObjectDto>();
        var storyCount = ComputeStoryCount(userObjects);

        foreach (var obj in userObjects)
        {
            var storyAssignments = ResolveStoryAssignments(obj, storyCount);

            // Coalesce consecutive same-type stories into spanning wall runs:
            // a 3-story column of identical walls becomes one Revit wall from
            // L1's base to L4 instead of three stacked instances. Floors and
            // ceilings remain per-story (one slab per level) — see EmitRun.
            foreach (var run in CoalesceRuns(storyAssignments))
            {
                EmitRun(obj, run, walls, floors, ceilings, stats);
            }
        }

        var before = walls.Count;
        DeduplicateEdges(walls);
        stats.EdgesDeduped = before - walls.Count;
        var centerX = double.NaN;
        var centerY = double.NaN;
        ComputeBoundingCenter(walls, floors, ceilings, ref centerX, ref centerY);
        if (!double.IsNaN(centerX))
        {
            RecenterWalls(walls, centerX, centerY);
            RecenterSlabs(floors, centerX, centerY);
            RecenterSlabs(ceilings, centerX, centerY);
        }

        var levelElevationsMm = ComputeLevelElevations(userObjects, storyCount);
        return new Result(walls, floors, ceilings, levelElevationsMm, stats);
    }

    // ── Story planning ───────────────────────────────────────────────────────

    private sealed record StoryAssignment(int StoryIndex, FloorTypeDto Type);

    private sealed record Run(int StartStory, int EndStory, FloorTypeDto Type);

    private static int ComputeStoryCount(List<UserObjectDto> userObjects)
    {
        var max = 0;
        foreach (var obj in userObjects)
        {
            var n = (obj.Floors is { Count: > 0 }) ? obj.Floors.Count : 1;
            if (n > max) max = n;
        }
        return max > 0 ? max : 1;
    }

    private static List<StoryAssignment> ResolveStoryAssignments(UserObjectDto obj, int storyCount)
    {
        var assignments = new List<StoryAssignment>(storyCount);
        var floors = (obj.Floors is { Count: > 0 }) ? obj.Floors : new List<int> { 0 };

        for (var storyIdx = 0; storyIdx < floors.Count; storyIdx++)
        {
            var type = PickType(obj.Types, floors[storyIdx]);
            if (type is null) continue;
            assignments.Add(new StoryAssignment(storyIdx, type));
        }
        return assignments;
    }

    /// <summary>
    /// Group consecutive <see cref="StoryAssignment"/>s sharing the same
    /// FloorType identity into a single <see cref="Run"/>. Identity is
    /// <see cref="FloorTypeDto.Id"/> when present (the editor's stable
    /// `_id`); otherwise the FloorTypeDto instance itself, so different
    /// stories pointing at different in-memory type objects don't get
    /// silently coalesced.
    /// </summary>
    private static IEnumerable<Run> CoalesceRuns(List<StoryAssignment> assignments)
    {
        if (assignments.Count == 0) yield break;

        static object KeyOf(FloorTypeDto t)
            => string.IsNullOrEmpty(t.Id) ? (object)t : t.Id!;

        var runStart = assignments[0].StoryIndex;
        var runType = assignments[0].Type;
        var runKey = KeyOf(runType);
        var prevStory = runStart;

        for (var i = 1; i < assignments.Count; i++)
        {
            var a = assignments[i];
            var key = KeyOf(a.Type);
            // Require strictly consecutive story indices AND identical type
            // — a gap or a type change ends the run.
            if (Equals(key, runKey) && a.StoryIndex == prevStory + 1)
            {
                prevStory = a.StoryIndex;
                continue;
            }
            yield return new Run(runStart, prevStory, runType);
            runStart = a.StoryIndex;
            runType = a.Type;
            runKey = key;
            prevStory = a.StoryIndex;
        }
        yield return new Run(runStart, prevStory, runType);
    }

    // ── Emit one run (walls + per-story floors/ceilings) ─────────────────────

    private static void EmitRun(
        UserObjectDto obj,
        Run run,
        List<WallDto> walls,
        List<FloorDto> floors,
        List<CeilingDto> ceilings,
        BuildStats stats)
    {
        var type = run.Type;

        var heightMm = ToMm(type.Height);
        if (heightMm <= 0) heightMm = DefaultStoryHeightMm;

        var thicknessMm = ToMm(type.Thickness);
        if (thicknessMm <= 0) thicknessMm = 200;

        var colorHex = string.IsNullOrWhiteSpace(type.Color) ? null : type.Color;
        var typeName = string.IsNullOrWhiteSpace(type.Name) ? "Generic" : type.Name!;
        var typeId = string.IsNullOrWhiteSpace(type.Id) ? null : type.Id;

        var baseLevel = LevelName(run.StartStory);
        var topLevel = LevelName(run.EndStory + 1);
        var spanStories = run.EndStory - run.StartStory + 1;
        var spanHeightMm = heightMm * spanStories;

        // Walls: prefer the editor's segment DSL (`type.walls` = array of
        // {start, end, depth, baseline, kind?}). If `type.walls` is still in
        // the legacy polygon shape — or absent — fall back to polygon edges
        // from `type.walls` / `obj.polygonPoints`.
        if (IsWallSegmentList(type.Walls))
        {
            stats.SegmentObjects++;
            EmitWallSegments(type.Walls, baseLevel, topLevel, typeName, typeId,
                spanHeightMm, thicknessMm, colorHex, walls, stats);
        }
        else
        {
            stats.PolygonObjects++;
            var wallSource = HasContent(type.Walls) ? type.Walls : obj.PolygonPoints;
            foreach (var polygon in NormalizePolygons(wallSource))
            {
                EmitWallPolygon(polygon, baseLevel, topLevel, typeName, typeId,
                    spanHeightMm, thicknessMm, colorHex, walls);
            }
        }

        // Floors + ceilings stay per story so each level gets its own slab.
        var floorPolygons = HasContent(type.Floor)
            ? NormalizePolygons(type.Floor).ToList()
            : new List<Polygon>();
        var ceilingPolygons = HasContent(type.Ceiling)
            ? NormalizePolygons(type.Ceiling).ToList()
            : new List<Polygon>();

        for (var storyIdx = run.StartStory; storyIdx <= run.EndStory; storyIdx++)
        {
            var hostLevel = LevelName(storyIdx);

            foreach (var polygon in floorPolygons)
            {
                var (outer, holes) = RingsToMm(polygon);
                if (outer.Length < 3) continue;
                floors.Add(new FloorDto
                {
                    Type = typeName,
                    TypeId = typeId,
                    Level = hostLevel,
                    ThicknessMm = DefaultSlabThicknessMm,
                    ColorHex = colorHex,
                    Outer = outer,
                    Holes = holes,
                });
            }

            foreach (var polygon in ceilingPolygons)
            {
                var (outer, holes) = RingsToMm(polygon);
                if (outer.Length < 3) continue;
                ceilings.Add(new CeilingDto
                {
                    Type = typeName,
                    TypeId = typeId,
                    Level = hostLevel,
                    ThicknessMm = DefaultSlabThicknessMm,
                    ColorHex = colorHex,
                    HeightOffsetMm = heightMm,
                    Outer = outer,
                    Holes = holes,
                });
            }
        }
    }

    private static string LevelName(int zeroBasedIdx) => $"Level {zeroBasedIdx + 1}";

    // ── Wall segments (new DSL: `[{start,end,depth,baseline,kind}, …]`) ──────

    /// <summary>
    /// True when <paramref name="data"/> looks like the editor's per-wall
    /// segment array: objects with `start` and `end` fields, each holding a
    /// 2D point in either array (`[x, y]`) or object (`{x, y}`) form. The
    /// legacy polygon shape is `[{x,y}, …]` or arrays of those, never
    /// `{start, end}`.
    /// </summary>
    private static bool IsWallSegmentList(JsonElement data)
    {
        if (!HasContent(data)) return false;
        var first = data[0];
        if (first.ValueKind != JsonValueKind.Object) return false;
        return IsPointField(first, "start") && IsPointField(first, "end");
    }

    private static bool IsPointField(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Array
            && v.GetArrayLength() >= 2
            && v[0].ValueKind == JsonValueKind.Number
            && v[1].ValueKind == JsonValueKind.Number)
            return true;
        if (v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number
            && v.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number)
            return true;
        return false;
    }

    private static bool TryReadPoint(JsonElement obj, string name, out double x, out double y)
    {
        x = 0; y = 0;
        if (!obj.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Array
            && v.GetArrayLength() >= 2
            && v[0].ValueKind == JsonValueKind.Number
            && v[1].ValueKind == JsonValueKind.Number)
        {
            x = v[0].GetDouble();
            y = v[1].GetDouble();
            return true;
        }
        if (v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("x", out var xEl) && xEl.ValueKind == JsonValueKind.Number
            && v.TryGetProperty("y", out var yEl) && yEl.ValueKind == JsonValueKind.Number)
        {
            x = xEl.GetDouble();
            y = yEl.GetDouble();
            return true;
        }
        return false;
    }

    private static void EmitWallSegments(
        JsonElement segments,
        string baseLevel,
        string topLevel,
        string typeName,
        string? typeId,
        double heightMm,
        double fallbackThicknessMm,
        string? colorHex,
        List<WallDto> walls,
        BuildStats stats)
    {
        foreach (var seg in segments.EnumerateArray())
        {
            if (seg.ValueKind != JsonValueKind.Object
                || !TryReadPoint(seg, "start", out var sx, out var sy)
                || !TryReadPoint(seg, "end",   out var ex, out var ey))
            {
                stats.SegmentsDroppedMalformed++;
                continue;
            }
            stats.SegmentsRead++;

            var startMm = new[] { ToMm(sx), ToMm(sy) };
            var endMm   = new[] { ToMm(ex), ToMm(ey) };

            var dx = endMm[0] - startMm[0];
            var dy = endMm[1] - startMm[1];
            if (dx * dx + dy * dy < MinEdgeLengthMm * MinEdgeLengthMm)
            {
                stats.SegmentsDroppedShort++;
                continue;
            }

            // Per-wall thickness from the segment; fall back to the type's
            // thickness when the editor didn't tag it (older partial exports).
            var thicknessMm = fallbackThicknessMm;
            if (seg.TryGetProperty("depth", out var depthEl) && depthEl.ValueKind == JsonValueKind.Number)
            {
                var d = ToMm(depthEl.GetDouble());
                if (d > 0) thicknessMm = d;
            }

            // Baseline is normally an integer in {−1, 0, 1}; treat anything ≥ 0.5
            // as +1, ≤ −0.5 as −1, otherwise 0 (centered) so a future float
            // value doesn't break the wall location-line mapping.
            var baseline = 0;
            if (seg.TryGetProperty("baseline", out var bEl) && bEl.ValueKind == JsonValueKind.Number)
            {
                var b = bEl.GetDouble();
                if (b >= 0.5) baseline = 1;
                else if (b <= -0.5) baseline = -1;
            }

            string? kind = null;
            if (seg.TryGetProperty("kind", out var kEl) && kEl.ValueKind == JsonValueKind.String)
                kind = kEl.GetString();

            walls.Add(new WallDto
            {
                Type = typeName,
                TypeId = typeId,
                Level = baseLevel,
                TopLevel = topLevel,
                Start = startMm,
                End = endMm,
                Height = heightMm,
                ThicknessMm = thicknessMm,
                ColorHex = colorHex,
                Baseline = baseline,
                Kind = kind,
            });
        }
    }

    // ── Polygon → wall edges ─────────────────────────────────────────────────

    private static void EmitWallPolygon(
        Polygon polygon,
        string baseLevel,
        string topLevel,
        string typeName,
        string? typeId,
        double heightMm,
        double thicknessMm,
        string? colorHex,
        List<WallDto> walls)
    {
        // Revit's wall orientation with flip=false puts the exterior face on the
        // LEFT of the curve direction (orientation = Z × curveDir). Walking the
        // outer ring clockwise and holes counter-clockwise makes the exterior
        // face land on the correct side for each — outside-of-building for the
        // outer ring, inside-of-courtyard for holes — so no per-wall flipping
        // is needed at creation time.
        EmitRingEdges(NormalizeWinding(polygon.Outer, wantCcw: false),
            baseLevel, topLevel, typeName, typeId, heightMm, thicknessMm, colorHex, walls);

        foreach (var hole in polygon.Holes)
        {
            EmitRingEdges(NormalizeWinding(hole, wantCcw: true),
                baseLevel, topLevel, typeName, typeId, heightMm, thicknessMm, colorHex, walls);
        }
    }

    private static void EmitRingEdges(
        Point2D[] ring,
        string baseLevel,
        string topLevel,
        string typeName,
        string? typeId,
        double heightMm,
        double thicknessMm,
        string? colorHex,
        List<WallDto> walls)
    {
        var merged = MergeCollinear(ring);
        if (merged.Length < 2) return;

        for (var i = 0; i < merged.Length; i++)
        {
            var a = merged[i];
            var b = merged[(i + 1) % merged.Length];

            var startMm = new[] { ToMm(a.X), ToMm(a.Y) };
            var endMm = new[] { ToMm(b.X), ToMm(b.Y) };

            var dx = endMm[0] - startMm[0];
            var dy = endMm[1] - startMm[1];
            if (dx * dx + dy * dy < MinEdgeLengthMm * MinEdgeLengthMm) continue;

            walls.Add(new WallDto
            {
                Type = typeName,
                TypeId = typeId,
                Level = baseLevel,
                TopLevel = topLevel,
                Start = startMm,
                End = endMm,
                Height = heightMm,
                ThicknessMm = thicknessMm,
                ColorHex = colorHex,
                // Polygon emit pre-normalises winding (outer CW, holes CCW) so
                // the wall body always sits to the RIGHT of the curve — Revit's
                // interior side under flip=false. Baseline=+1 reproduces that
                // through the new generic baseline → location-line mapping.
                Baseline = 1,
            });
        }
    }

    private static (double[][] Outer, double[][][] Holes) RingsToMm(Polygon polygon)
    {
        var outer = RingToMm(MergeCollinear(polygon.Outer));
        if (polygon.Holes.Count == 0) return (outer, Array.Empty<double[][]>());

        var holes = new List<double[][]>(polygon.Holes.Count);
        foreach (var hole in polygon.Holes)
        {
            var merged = MergeCollinear(hole);
            if (merged.Length < 3) continue;
            holes.Add(RingToMm(merged));
        }
        return (outer, holes.ToArray());
    }

    private static double[][] RingToMm(Point2D[] ring)
    {
        var result = new double[ring.Length][];
        for (var i = 0; i < ring.Length; i++)
            result[i] = new[] { ToMm(ring[i].X), ToMm(ring[i].Y) };
        return result;
    }

    private static Point2D[] NormalizeWinding(Point2D[] ring, bool wantCcw)
    {
        if (ring.Length < 3) return ring;
        var isCcw = SignedArea(ring) > 0;
        if (isCcw == wantCcw) return ring;
        var reversed = new Point2D[ring.Length];
        for (var i = 0; i < ring.Length; i++)
            reversed[i] = ring[ring.Length - 1 - i];
        return reversed;
    }

    private static double SignedArea(Point2D[] ring)
    {
        double sum = 0;
        for (var i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2.0;
    }

    private static Point2D[] MergeCollinear(Point2D[] ring)
    {
        if (ring.Length < 3) return ring;

        var pts = new List<Point2D>(ring.Length);
        foreach (var p in ring)
        {
            if (pts.Count > 0 && AlmostEqual(pts[^1], p)) continue;
            pts.Add(p);
        }
        if (pts.Count > 1 && AlmostEqual(pts[0], pts[^1])) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return pts.ToArray();

        var changed = true;
        while (changed && pts.Count >= 3)
        {
            changed = false;
            for (var i = 0; i < pts.Count && pts.Count >= 3; i++)
            {
                var prev = pts[(i - 1 + pts.Count) % pts.Count];
                var cur = pts[i];
                var next = pts[(i + 1) % pts.Count];
                var cross = (cur.X - prev.X) * (next.Y - prev.Y)
                          - (cur.Y - prev.Y) * (next.X - prev.X);
                if (Math.Abs(cross) * 1_000_000 < CollinearToleranceMm2)
                {
                    pts.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }
        return pts.ToArray();
    }

    private static bool AlmostEqual(Point2D a, Point2D b)
        => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    // ── Cross-ring dedupe ────────────────────────────────────────────────────

    /// <summary>
    /// Drops edges that are identical between two rings of the same
    /// (baseLevel, topLevel, type, thickness, color). Canonicalises endpoints
    /// to a sort order so opposite-direction edges match.
    /// </summary>
    private static void DeduplicateEdges(List<WallDto> walls)
    {
        if (walls.Count < 2) return;

        var seen = new HashSet<string>(walls.Count);
        var keep = new List<WallDto>(walls.Count);
        foreach (var w in walls)
        {
            if (seen.Add(EdgeKey(w))) keep.Add(w);
        }
        walls.Clear();
        walls.AddRange(keep);
    }

    private static string EdgeKey(WallDto w)
    {
        long x1 = (long)Math.Round(w.Start[0] * 10);
        long y1 = (long)Math.Round(w.Start[1] * 10);
        long x2 = (long)Math.Round(w.End[0] * 10);
        long y2 = (long)Math.Round(w.End[1] * 10);
        if (x1 > x2 || (x1 == x2 && y1 > y2))
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }
        return $"{w.Level}>{w.TopLevel ?? ""}|{w.Type}|{w.ThicknessMm}|{w.Baseline}|{w.ColorHex ?? ""}|{x1},{y1}->{x2},{y2}";
    }

    // ── Level elevations ─────────────────────────────────────────────────────

    /// <summary>
    /// Emits one level per story PLUS one roof level at the top story's ceiling,
    /// so spanning walls have a valid top constraint.
    /// </summary>
    private static Dictionary<string, double> ComputeLevelElevations(
        List<UserObjectDto> userObjects,
        int storyCount)
    {
        var elevations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (storyCount == 0) return elevations;

        var heightByStory = new double[storyCount];
        foreach (var obj in userObjects)
        {
            var floors = (obj.Floors is { Count: > 0 }) ? obj.Floors : new List<int> { 0 };
            for (var storyIdx = 0; storyIdx < floors.Count && storyIdx < storyCount; storyIdx++)
            {
                var type = PickType(obj.Types, floors[storyIdx]);
                if (type is null) continue;

                var h = ToMm(type.Height);
                if (h <= 0) h = DefaultStoryHeightMm;
                if (h > heightByStory[storyIdx]) heightByStory[storyIdx] = h;
            }
        }

        double cumulative = 0;
        for (var i = 0; i < storyCount; i++)
        {
            elevations[LevelName(i)] = cumulative;
            var h = heightByStory[i] > 0 ? heightByStory[i] : DefaultStoryHeightMm;
            cumulative += h;
        }
        // Roof level above the top story — caps any spanning walls.
        elevations[LevelName(storyCount)] = cumulative;
        return elevations;
    }

    // ── Unit conversion / centering ──────────────────────────────────────────

    private static double ToMm(double? n) => Math.Round((n ?? 0) * 1000);
    private static double ToMm(double n) => Math.Round(n * 1000);

    private static void ComputeBoundingCenter(
        List<WallDto> walls,
        List<FloorDto> floors,
        List<CeilingDto> ceilings,
        ref double cx,
        ref double cy)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var any = false;

        foreach (var w in walls)
        {
            any = true;
            minX = Math.Min(minX, Math.Min(w.Start[0], w.End[0]));
            maxX = Math.Max(maxX, Math.Max(w.Start[0], w.End[0]));
            minY = Math.Min(minY, Math.Min(w.Start[1], w.End[1]));
            maxY = Math.Max(maxY, Math.Max(w.Start[1], w.End[1]));
        }
        any |= SlabBounds(floors,   ref minX, ref minY, ref maxX, ref maxY);
        any |= SlabBounds(ceilings, ref minX, ref minY, ref maxX, ref maxY);

        if (!any) return;
        cx = Math.Round((minX + maxX) / 2);
        cy = Math.Round((minY + maxY) / 2);
    }

    private static bool SlabBounds<T>(List<T> slabs,
        ref double minX, ref double minY, ref double maxX, ref double maxY)
        where T : SlabDto
    {
        var any = false;
        foreach (var s in slabs)
        {
            any |= RingBounds(s.Outer, ref minX, ref minY, ref maxX, ref maxY);
            foreach (var hole in s.Holes)
                any |= RingBounds(hole, ref minX, ref minY, ref maxX, ref maxY);
        }
        return any;
    }

    private static bool RingBounds(double[][] ring,
        ref double minX, ref double minY, ref double maxX, ref double maxY)
    {
        var any = false;
        foreach (var p in ring)
        {
            if (p.Length < 2) continue;
            any = true;
            minX = Math.Min(minX, p[0]);
            maxX = Math.Max(maxX, p[0]);
            minY = Math.Min(minY, p[1]);
            maxY = Math.Max(maxY, p[1]);
        }
        return any;
    }

    private static void RecenterWalls(List<WallDto> walls, double cx, double cy)
    {
        if (cx == 0 && cy == 0) return;
        foreach (var w in walls)
        {
            w.Start[0] -= cx; w.Start[1] -= cy;
            w.End[0]   -= cx; w.End[1]   -= cy;
        }
    }

    private static void RecenterSlabs<T>(List<T> slabs, double cx, double cy) where T : SlabDto
    {
        if (cx == 0 && cy == 0) return;
        foreach (var s in slabs)
        {
            RecenterRing(s.Outer, cx, cy);
            foreach (var hole in s.Holes) RecenterRing(hole, cx, cy);
        }
    }

    private static void RecenterRing(double[][] ring, double cx, double cy)
    {
        foreach (var p in ring)
        {
            if (p.Length < 2) continue;
            p[0] -= cx;
            p[1] -= cy;
        }
    }

    // ── Polygon parsing ──────────────────────────────────────────────────────

    private readonly record struct Point2D(double X, double Y);

    private sealed record Polygon(Point2D[] Outer, List<Point2D[]> Holes);

    private static bool HasContent(JsonElement e)
        => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0;

    private static bool IsPoint(JsonElement e)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number
           && e.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number;

    private static Point2D[] ParseRing(JsonElement ring)
    {
        if (ring.ValueKind != JsonValueKind.Array) return Array.Empty<Point2D>();
        var pts = new List<Point2D>();
        foreach (var p in ring.EnumerateArray())
        {
            if (!IsPoint(p)) continue;
            pts.Add(new Point2D(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble()));
        }
        return pts.ToArray();
    }

    /// <summary>
    /// Accepts:
    ///   • [] / undefined               → nothing
    ///   • [{x,y}, ...]                 → single polygon, flat ring
    ///   • [[{x,y},...], ...]           → one ring per polygon (simple rings)
    ///   • [[[outer],[hole],...], ...]  → polygons with holes
    /// </summary>
    private static IEnumerable<Polygon> NormalizePolygons(JsonElement data)
    {
        if (!HasContent(data)) yield break;

        var first = data[0];

        if (IsPoint(first))
        {
            var ring = ParseRing(data);
            if (ring.Length >= 3) yield return new Polygon(ring, new());
            yield break;
        }

        foreach (var polygon in data.EnumerateArray())
        {
            if (polygon.ValueKind != JsonValueKind.Array || polygon.GetArrayLength() == 0)
                continue;

            var polyFirst = polygon[0];
            if (IsPoint(polyFirst))
            {
                var ring = ParseRing(polygon);
                if (ring.Length >= 3) yield return new Polygon(ring, new());
                continue;
            }

            var holes = new List<Point2D[]>();
            Point2D[]? outer = null;
            foreach (var ring in polygon.EnumerateArray())
            {
                var parsed = ParseRing(ring);
                if (parsed.Length < 3) continue;
                if (outer is null) outer = parsed;
                else holes.Add(parsed);
            }
            if (outer is not null) yield return new Polygon(outer, holes);
        }
    }

    private static FloorTypeDto? PickType(List<FloorTypeDto>? types, int idx)
    {
        if (types is null || types.Count == 0) return null;
        if (idx >= 0 && idx < types.Count) return types[idx];
        return types[0];
    }
}
