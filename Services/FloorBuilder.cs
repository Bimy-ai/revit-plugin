using Autodesk.Revit.DB;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal static class FloorBuilder
{
    public sealed record Result(
        int Created,
        HashSet<string> CreatedFloorTypes,
        List<string> Errors);

    public static Result CreateFloors(
        Document doc,
        List<FloorDto> floors,
        Dictionary<string, Level> levelByName,
        ImportMaterialProvider materials,
        string importTag)
    {
        var typeProvider = new FloorTypeProvider(doc, materials);

        var created = 0;
        var errors = new List<string>();
        for (var i = 0; i < floors.Count; i++)
        {
            try
            {
                if (CreateOneFloor(doc, floors[i], levelByName, typeProvider, importTag))
                    created++;
            }
            catch (Exception ex)
            {
                errors.Add($"Floor #{i + 1}: {ex.Message}");
            }
        }

        return new Result(created, typeProvider.CreatedTypes, errors);
    }

    private static bool CreateOneFloor(
        Document doc,
        FloorDto dto,
        Dictionary<string, Level> levelByName,
        FloorTypeProvider typeProvider,
        string importTag)
    {
        if (string.IsNullOrWhiteSpace(dto.Level)) throw new InvalidOperationException("Missing 'level'.");
        if (dto.Outer is null || dto.Outer.Length < 3) throw new InvalidOperationException("Outer ring needs ≥ 3 points.");

        var floorType = typeProvider.Get(dto.TypeId, dto.Type, dto.ThicknessMm, dto.ColorHex);
        var level = RevitLookup.ResolveLevel(levelByName, dto.Level!.Trim());

        var loops = SlabGeometry.BuildCurveLoops(dto.Outer, dto.Holes);
        if (loops.Count == 0) throw new InvalidOperationException("Outer ring collapses to < 3 distinct points.");

        var floor = Floor.Create(doc, loops, floorType.Id, level.Id);

        ParamSet.TrySet(floor, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, importTag);
        return true;
    }
}
