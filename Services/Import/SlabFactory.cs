using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

/// <summary>
/// Floors and ceilings from payload rings — `Floor.Create` / `Ceiling.Create`
/// with the holes as inner loops, lifted to the storey-relative offsets the app
/// resolved (a dropped threshold slab or a hung ceiling arrives exactly where
/// the app drew it). A floor with a slope record takes the slope-arrow
/// overload, which is Revit's native way to say "this slab falls".
/// </summary>
internal static class SlabFactory
{
    public static void Build(BuildContext ctx)
    {
        foreach (var f in ctx.Model.Floors) BuildFloor(ctx, f);
        foreach (var c in ctx.Model.Ceilings) BuildCeiling(ctx, c);
    }

    private static void BuildFloor(BuildContext ctx, Models.SlabDto dto)
    {
        try
        {
            var level = ctx.LevelAt(dto.Storey);
            var loops = ImportGeometry.Loops(dto.Ring, dto.Holes, 0);
            if (loops.Count == 0)
            {
                ctx.Warn($"Floor {dto.Id}: ring collapses to fewer than 3 points.");
                return;
            }
            var type = TypeFactory.FloorTypeFor(ctx, dto);

            Floor floor;
            if (dto.Slope is not null && Math.Abs(dto.Slope.Pitch) > 1e-6)
            {
                // Tail at the reference point, head one metre uphill; slope is
                // rise/run, exactly the payload's `pitch`.
                var tail = ImportGeometry.Pt(dto.Slope.RefMm, 0);
                var head = tail + new XYZ(dto.Slope.Uphill[0], dto.Slope.Uphill[1], 0)
                    .Normalize() * ImportGeometry.Ft(1000);
                try
                {
                    floor = Floor.Create(ctx.Doc, loops, type.Id, level.Id,
                        false, Line.CreateBound(tail, head), dto.Slope.Pitch);
                }
                catch (Exception ex)
                {
                    ctx.Warn($"Floor {dto.Id}: slope rejected ({ex.Message}) — built flat.");
                    floor = Floor.Create(ctx.Doc, loops, type.Id, level.Id);
                }
            }
            else
            {
                floor = Floor.Create(ctx.Doc, loops, type.Id, level.Id);
            }

            // The payload states the slab's UNDERSIDE above the level; Revit's
            // offset positions the floor's TOP face.
            ParamSet.TrySet(floor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,
                ImportGeometry.Ft(dto.ZOffsetMm + dto.ThicknessMm));

            ctx.Brand(floor, dto.Guid ?? dto.Id);
            if (!string.IsNullOrWhiteSpace(dto.Name))
                ParamSet.TrySet(floor, BuiltInParameter.ALL_MODEL_MARK, dto.Name!);
            ctx.Count("Floors");
        }
        catch (Exception ex)
        {
            ctx.Warn($"Floor {dto.Id}: {ex.Message}");
        }
    }

    private static void BuildCeiling(BuildContext ctx, Models.SlabDto dto)
    {
        try
        {
            var level = ctx.LevelAt(dto.Storey);
            var loops = ImportGeometry.Loops(dto.Ring, dto.Holes, 0);
            if (loops.Count == 0)
            {
                ctx.Warn($"Ceiling {dto.Id}: ring collapses to fewer than 3 points.");
                return;
            }
            var type = TypeFactory.CeilingTypeFor(ctx, dto);
            var ceiling = Ceiling.Create(ctx.Doc, loops, type.Id, level.Id);

            // Revit measures a ceiling's offset to its UNDERSIDE — the same
            // face the payload states, no thickness correction needed.
            ParamSet.TrySet(ceiling, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM,
                ImportGeometry.Ft(dto.ZOffsetMm));

            ctx.Brand(ceiling, dto.Guid ?? dto.Id);
            if (!string.IsNullOrWhiteSpace(dto.Name))
                ParamSet.TrySet(ceiling, BuiltInParameter.ALL_MODEL_MARK, dto.Name!);
            ctx.Count("Ceilings");
        }
        catch (Exception ex)
        {
            ctx.Warn($"Ceiling {dto.Id}: {ex.Message}");
        }
    }
}
