using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

/// <summary>
/// Rooms. The app derives them from the wall graph and the payload hands each
/// one a point strictly inside its boundary — this pass places a real Revit
/// Room there (the walls built earlier are room-bounding, so Revit floods to
/// the same boundary), then names, numbers and height-limits it. A room whose
/// walls did not enclose (a degenerate edge got skipped) still exists, marked
/// unenclosed by Revit — honest, and visible in the schedule rather than gone.
/// </summary>
internal static class RoomFactory
{
    public static void Build(BuildContext ctx)
    {
        if (ctx.Model.Rooms.Count == 0) return;

        // Rooms flood against the CURRENT regenerated model — the walls were
        // committed by their own transaction, but inside ours nothing has
        // regenerated yet.
        try { ctx.Doc.Regenerate(); } catch { /* non-fatal */ }

        foreach (var r in ctx.Model.Rooms)
        {
            try
            {
                var level = ctx.LevelAt(r.Storey);
                var at = new UV(ImportGeometry.Ft(r.Point[0]), ImportGeometry.Ft(r.Point[1]));
                var room = ctx.Doc.Create.NewRoom(level, at);
                if (room is null)
                {
                    ctx.Warn($"Room {r.Id}: Revit declined to place it.");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(r.Name))
                {
                    try { room.Name = r.Name!; } catch { /* non-fatal */ }
                }
                if (!string.IsNullOrWhiteSpace(r.Number))
                {
                    try { room.Number = r.Number!; } catch { /* duplicate number — keep Revit's */ }
                }
                if (r.HeightMm > 0)
                    ParamSet.TrySet(room, BuiltInParameter.ROOM_UPPER_OFFSET, ImportGeometry.Ft(r.HeightMm));
                ctx.Brand(room, r.Guid ?? r.Id);
                ctx.Count("Rooms");
            }
            catch (Exception ex)
            {
                ctx.Warn($"Room {r.Id}: {ex.Message}");
            }
        }
    }
}
