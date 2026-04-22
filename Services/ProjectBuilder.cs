using System.Text.Json;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Converts the raw <see cref="UserObjectsPayload"/> returned by the API
/// into a flat list of centered, millimetre-scaled <see cref="WallDto"/>s
/// ready for Revit wall creation.
///
/// Mirrors the previous frontend logic (src/lib/buildProjectData.js +
/// polygonHelpers.js) now that the API only ships raw userObjects.
/// </summary>
internal static class ProjectBuilder
{
    public sealed record Result(
        List<WallDto> Walls,
        Dictionary<string, double> LevelElevationsMm);

    public static Result BuildWalls(UserObjectsPayload payload)
    {
        var walls = new List<WallDto>();
        var userObjects = payload.UserObjects ?? new List<UserObjectDto>();

        foreach (var obj in userObjects)
        {
            var floors = (obj.Floors is { Count: > 0 }) ? obj.Floors : new List<int> { 0 };

            for (var floorIdx = 0; floorIdx < floors.Count; floorIdx++)
            {
                var typeIdx = floors[floorIdx];
                var type = PickType(obj.Types, typeIdx);
                if (type is null) continue;

                var level = $"L{floorIdx + 1}";
                var wallSource = HasContent(type.Walls) ? type.Walls : obj.PolygonPoints;
                var rings = NormalizePolygons(wallSource);

                var heightMm = ToMm(type.Height);
                if (heightMm <= 0) heightMm = 3000;

                var thicknessMm = ToMm(type.Thickness);
                if (thicknessMm <= 0) thicknessMm = 200; // planConfig.externalWallThickness (0.2 m)

                var colorHex = string.IsNullOrWhiteSpace(type.Color) ? null : type.Color;

                foreach (var ring in rings)
                {
                    if (ring.Length < 2) continue;

                    for (var i = 0; i < ring.Length; i++)
                    {
                        var a = ring[i];
                        var b = ring[(i + 1) % ring.Length];

                        walls.Add(new WallDto
                        {
                            Type = string.IsNullOrWhiteSpace(type.Name) ? "Generic" : type.Name!,
                            Level = level,
                            Start = new[] { ToMm(a.X), ToMm(a.Y) },
                            End   = new[] { ToMm(b.X), ToMm(b.Y) },
                            Height = heightMm,
                            ThicknessMm = thicknessMm,
                            ColorHex = colorHex,
                        });
                    }
                }
            }
        }

        CenterInPlace(walls);
        var levelElevationsMm = ComputeLevelElevations(userObjects);
        return new Result(walls, levelElevationsMm);
    }

    /// <summary>
    /// Builds a global elevation map (L1, L2, …) by stacking each floor's height.
    /// When multiple userObjects disagree on a floor's height, the tallest wins so
    /// walls on that floor fit cleanly under the next level.
    /// </summary>
    private static Dictionary<string, double> ComputeLevelElevations(List<UserObjectDto> userObjects)
    {
        var maxHeightByFloor = new Dictionary<int, double>();

        foreach (var obj in userObjects)
        {
            var floors = (obj.Floors is { Count: > 0 }) ? obj.Floors : new List<int> { 0 };
            for (var floorIdx = 0; floorIdx < floors.Count; floorIdx++)
            {
                var type = PickType(obj.Types, floors[floorIdx]);
                if (type is null) continue;

                var h = ToMm(type.Height);
                if (h <= 0) h = 3000;

                if (!maxHeightByFloor.TryGetValue(floorIdx, out var existing) || h > existing)
                    maxHeightByFloor[floorIdx] = h;
            }
        }

        var elevations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (maxHeightByFloor.Count == 0) return elevations;

        var lastFloorIdx = maxHeightByFloor.Keys.Max();
        double cumulative = 0;
        for (var i = 0; i <= lastFloorIdx; i++)
        {
            elevations[$"L{i + 1}"] = cumulative;
            cumulative += maxHeightByFloor.TryGetValue(i, out var h) ? h : 3000;
        }
        return elevations;
    }

    // ── Unit conversion ──────────────────────────────────────────────────────

    // Source coordinates / heights are in meters (editor/building.js).
    // JSON-to-Revit contract is millimetres.
    private static double ToMm(double? n) => Math.Round((n ?? 0) * 1000);
    private static double ToMm(double n)  => Math.Round(n * 1000);

    // ── Bounding-box centering ───────────────────────────────────────────────

    private static void CenterInPlace(List<WallDto> walls)
    {
        if (walls.Count == 0) return;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        foreach (var w in walls)
        {
            minX = Math.Min(minX, Math.Min(w.Start[0], w.End[0]));
            maxX = Math.Max(maxX, Math.Max(w.Start[0], w.End[0]));
            minY = Math.Min(minY, Math.Min(w.Start[1], w.End[1]));
            maxY = Math.Max(maxY, Math.Max(w.Start[1], w.End[1]));
        }

        var cx = Math.Round((minX + maxX) / 2);
        var cy = Math.Round((minY + maxY) / 2);
        if (cx == 0 && cy == 0) return;

        foreach (var w in walls)
        {
            w.Start[0] -= cx; w.Start[1] -= cy;
            w.End[0]   -= cx; w.End[1]   -= cy;
        }
    }

    // ── Polygon normalization (port of polygonHelpers.js) ────────────────────

    private readonly record struct Point2D(double X, double Y);

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
    /// Yield the outer ring of every polygon in <paramref name="data"/>.
    ///
    /// Accepts:
    ///  • [] / undefined               → nothing
    ///  • [{x,y}, ...]                 → single ring (legacy flat format)
    ///  • [[{x,y},...], [{x,y},...]]   → one ring per polygon (simple rings)
    ///  • [[[outer],[hole],...], ...]  → one outer ring per polygon (ignores holes)
    /// </summary>
    private static IEnumerable<Point2D[]> NormalizePolygons(JsonElement data)
    {
        if (!HasContent(data)) yield break;

        var first = data[0];

        // Old format: flat [{x,y}, ...]
        if (IsPoint(first))
        {
            var ring = ParseRing(data);
            if (ring.Length > 0) yield return ring;
            yield break;
        }

        // New format: array of polygons.
        foreach (var polygon in data.EnumerateArray())
        {
            if (polygon.ValueKind != JsonValueKind.Array || polygon.GetArrayLength() == 0)
                continue;

            var polyFirst = polygon[0];
            var outer = IsPoint(polyFirst)
                ? ParseRing(polygon)    // simple ring
                : ParseRing(polyFirst); // [outer, hole, …]
            if (outer.Length > 0) yield return outer;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static FloorTypeDto? PickType(List<FloorTypeDto>? types, int idx)
    {
        if (types is null || types.Count == 0) return null;
        if (idx >= 0 && idx < types.Count) return types[idx];
        return types[0];
    }
}
