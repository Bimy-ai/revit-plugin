using Autodesk.Revit.DB;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal static class WallBuilder
{
    public sealed record Result(
        int Deleted,
        int Created,
        HashSet<string> CreatedLevels,
        HashSet<string> CreatedWallTypes,
        List<string> Errors);

    public static Result CreateWalls(Document doc, List<WallDto> walls)
    {
        var deleted = DeleteAllWalls(doc);

        var referencedLevels = walls
            .Select(w => w.Level)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToList();

        var (levelByName, createdLevels) = RevitLookup.EnsureLevels(doc, referencedLevels);
        var typeProvider = new WallTypeProvider(doc);

        var created = 0;
        var errors = new List<string>();

        for (var i = 0; i < walls.Count; i++)
        {
            try
            {
                if (CreateOneWall(doc, walls[i], levelByName, typeProvider))
                    created++;
            }
            catch (Exception ex)
            {
                errors.Add($"Wall #{i + 1}: {ex.Message}");
            }
        }

        return new Result(deleted, created, createdLevels, typeProvider.CreatedWallTypes, errors);
    }

    private static int DeleteAllWalls(Document doc)
    {
        var ids = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .ToElementIds();

        if (ids.Count == 0) return 0;

        var deleted = doc.Delete(ids);
        return deleted?.Count ?? 0;
    }

    private static bool CreateOneWall(
        Document doc,
        WallDto dto,
        Dictionary<string, Level> levelByName,
        WallTypeProvider typeProvider)
    {
        if (string.IsNullOrWhiteSpace(dto.Level)) throw new InvalidOperationException("Missing 'level'.");
        if (dto.Start is null || dto.Start.Length < 2) throw new InvalidOperationException("'start' must be [x, y] in mm.");
        if (dto.End   is null || dto.End.Length   < 2) throw new InvalidOperationException("'end' must be [x, y] in mm.");
        if (dto.Height <= 0) throw new InvalidOperationException("'height' must be > 0 mm.");

        var wallType = typeProvider.Get(dto.ColorHex);
        var level = RevitLookup.ResolveLevel(levelByName, dto.Level!.Trim());

        var start = new XYZ(MmToFeet(dto.Start[0]), MmToFeet(dto.Start[1]), 0);
        var end   = new XYZ(MmToFeet(dto.End[0]),   MmToFeet(dto.End[1]),   0);

        if (start.IsAlmostEqualTo(end))
            throw new InvalidOperationException("Start and end points are identical.");

        var line = Line.CreateBound(start, end);
        var heightFeet = MmToFeet(dto.Height);

        Wall.Create(
            document: doc,
            curve: line,
            wallTypeId: wallType.Id,
            levelId: level.Id,
            height: heightFeet,
            offset: 0,
            flip: false,
            structural: false);

        return true;
    }

    private static double MmToFeet(double mm)
        => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
