using Autodesk.Revit.DB;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal static class CeilingBuilder
{
    public sealed record Result(
        int Created,
        HashSet<string> CreatedCeilingTypes,
        List<string> Errors);

    public static Result CreateCeilings(
        Document doc,
        List<CeilingDto> ceilings,
        Dictionary<string, Level> levelByName,
        ImportMaterialProvider materials,
        string importTag)
    {
        var typeProvider = new CeilingTypeProvider(doc, materials);

        var created = 0;
        var errors = new List<string>();
        for (var i = 0; i < ceilings.Count; i++)
        {
            try
            {
                if (CreateOneCeiling(doc, ceilings[i], levelByName, typeProvider, importTag))
                    created++;
            }
            catch (Exception ex)
            {
                errors.Add($"Ceiling #{i + 1}: {ex.Message}");
            }
        }

        return new Result(created, typeProvider.CreatedTypes, errors);
    }

    private static bool CreateOneCeiling(
        Document doc,
        CeilingDto dto,
        Dictionary<string, Level> levelByName,
        CeilingTypeProvider typeProvider,
        string importTag)
    {
        if (string.IsNullOrWhiteSpace(dto.Level)) throw new InvalidOperationException("Missing 'level'.");
        if (dto.Outer is null || dto.Outer.Length < 3) throw new InvalidOperationException("Outer ring needs ≥ 3 points.");

        var ceilingType = typeProvider.Get(dto.TypeId, dto.Type, dto.ThicknessMm, dto.ColorHex);
        var level = RevitLookup.ResolveLevel(levelByName, dto.Level!.Trim());

        var loops = SlabGeometry.BuildCurveLoops(dto.Outer, dto.Holes);
        if (loops.Count == 0) throw new InvalidOperationException("Outer ring collapses to < 3 distinct points.");

        var ceiling = Ceiling.Create(doc, loops, ceilingType.Id, level.Id);

        // Lift the ceiling to sit just under the next story's floor. The
        // offset lives on the instance, not the type, so multi-story ceilings
        // can share a CeilingType even when their heights differ.
        if (dto.HeightOffsetMm > 0)
        {
            var offsetFeet = UnitUtils.ConvertToInternalUnits(dto.HeightOffsetMm, UnitTypeId.Millimeters);
            ParamSet.TrySet(ceiling, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM, offsetFeet);
        }

        ParamSet.TrySet(ceiling, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, importTag);
        return true;
    }
}
