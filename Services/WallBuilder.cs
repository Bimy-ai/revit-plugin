using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal static class WallBuilder
{
    public sealed record Result(
        int Created,
        HashSet<string> CreatedWallTypes,
        List<string> Errors);

    public static Result CreateWalls(
        Document doc,
        List<WallDto> walls,
        Dictionary<string, Level> levelByName,
        ImportMaterialProvider materials,
        string importTag)
    {
        var typeProvider = new WallTypeProvider(doc, materials);

        var levelsByElevation = levelByName.Values
            .OrderBy(l => l.Elevation)
            .ToList();

        var created = 0;
        var errors = new List<string>();

        for (var i = 0; i < walls.Count; i++)
        {
            try
            {
                if (CreateOneWall(doc, walls[i], levelByName, levelsByElevation, typeProvider, importTag))
                    created++;
            }
            catch (Exception ex)
            {
                errors.Add($"Wall #{i + 1}: {ex.Message}");
            }
        }

        return new Result(created, typeProvider.CreatedWallTypes, errors);
    }

    private static bool CreateOneWall(
        Document doc,
        WallDto dto,
        Dictionary<string, Level> levelByName,
        List<Level> levelsByElevation,
        WallTypeProvider typeProvider,
        string importTag)
    {
        if (string.IsNullOrWhiteSpace(dto.Level)) throw new InvalidOperationException("Missing 'level'.");
        if (dto.Start is null || dto.Start.Length < 2) throw new InvalidOperationException("'start' must be [x, y] in mm.");
        if (dto.End is null || dto.End.Length < 2) throw new InvalidOperationException("'end' must be [x, y] in mm.");
        if (dto.Height <= 0) throw new InvalidOperationException("'height' must be > 0 mm.");

        var wallType = typeProvider.Get(dto.ThicknessMm);
        var level = RevitLookup.ResolveLevel(levelByName, dto.Level!.Trim());
        Level? explicitTop = null;
        if (!string.IsNullOrWhiteSpace(dto.TopLevel)
            && levelByName.TryGetValue(dto.TopLevel!.Trim(), out var top)
            && top.Elevation > level.Elevation + 0.004)
        {
            explicitTop = top;
        }

        var start = new XYZ(MmToFeet(dto.Start[0]), MmToFeet(dto.Start[1]), 0);
        var end = new XYZ(MmToFeet(dto.End[0]), MmToFeet(dto.End[1]), 0);

        if (start.IsAlmostEqualTo(end))
            throw new InvalidOperationException("Start and end points are identical.");

        var line = Line.CreateBound(start, end);
        var heightFeet = MmToFeet(dto.Height);

        var wall = Wall.Create(
            document: doc,
            curve: line,
            wallTypeId: wallType.Id,
            levelId: level.Id,
            height: heightFeet,
            offset: 0,
            flip: false,
            structural: false);

        ConfigureInstance(wall, dto, level, explicitTop, levelsByElevation, heightFeet, importTag);
        return true;
    }

    private static void ConfigureInstance(
        Wall wall,
        WallDto dto,
        Level baseLevel,
        Level? explicitTop,
        List<Level> levelsByElevation,
        double heightFeet,
        string importTag)
    {
        // Pin the location line so the wall body lands where the editor put
        // it. The segment DSL carries `baseline` per wall (−1/0/+1); the
        // editor's `+normal = (dir.y, −dir.x)` is the RIGHT of direction,
        // which equals Revit's interior side when flip=false. So:
        //   +1 → body on interior → line on exterior face → FinishFaceExterior
        //    0 → centered                                  → WallCenterline
        //   −1 → body on exterior → line on interior face → FinishFaceInterior
        // Legacy polygon-derived walls leave Baseline at 0; the old default of
        // anchoring to the exterior face only worked because polygon winding
        // was pre-normalized to make it correct, and that path is gone now.
        var locationLine = dto.Baseline switch
        {
            > 0 => WallLocationLine.FinishFaceExterior,
            < 0 => WallLocationLine.FinishFaceInterior,
            _   => WallLocationLine.WallCenterline,
        };
        ParamSet.TrySet(wall, BuiltInParameter.WALL_KEY_REF_PARAM, (int)locationLine);

        // Structural usage from the segment DSL's `kind`. "structural" → bearing,
        // "partition" → non-bearing. Unset on legacy polygon walls and on
        // segments without a kind tag — leave whatever the Wall.Create default
        // chose (NonBearing for structural=false).
        if (!string.IsNullOrWhiteSpace(dto.Kind))
        {
            var usage = dto.Kind!.Trim().ToLowerInvariant() switch
            {
                "structural" => (int)StructuralWallUsage.Bearing,
                "partition"  => (int)StructuralWallUsage.NonBearing,
                _ => (int?)null,
            };
            if (usage is not null)
                ParamSet.TrySet(wall, BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM, usage.Value);
        }

        // Prefer the caller-supplied top level (wall spans a known range of
        // stories); otherwise pick the next level above by elevation so
        // single-story walls still pick up a real top constraint.
        var topLevel = explicitTop
            ?? FindNextLevelAbove(levelsByElevation, baseLevel, heightFeet);
        if (topLevel is not null)
        {
            ParamSet.TrySet(wall, BuiltInParameter.WALL_HEIGHT_TYPE, topLevel.Id);
            ParamSet.TrySet(wall, BuiltInParameter.WALL_TOP_OFFSET, 0.0);
        }

        // Brand each imported wall so later re-imports can replace them
        // without clobbering user-drawn walls.
        ParamSet.TrySet(wall, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, importTag);
    }

    private static Level? FindNextLevelAbove(List<Level> levels, Level baseLevel, double heightFeet)
    {
        const double tolFeet = 0.004;
        var targetElev = baseLevel.Elevation + heightFeet;

        foreach (var l in levels)
        {
            if (l.Id == baseLevel.Id) continue;
            if (l.Elevation <= baseLevel.Elevation + tolFeet) continue;
            if (l.Elevation <= targetElev + tolFeet) return l;
            break;
        }
        return null;
    }

    private static double MmToFeet(double mm)
        => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
