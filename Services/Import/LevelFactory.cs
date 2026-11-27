using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

/// <summary>
/// Levels and their plan views. The build starts from the user's own project
/// template, which ships levels of its own ("Level 1"/"Level 2" or "L1"/"L2")
/// — those are REUSED where an elevation matches (renamed to the BIMy storey
/// name) and deleted where nothing needs them, so the Project Browser ends up
/// with exactly the building's storeys: one floor plan and one ceiling plan
/// per level, plus one extra level above the top storey that caps top-storey
/// walls and hosts nothing.
/// </summary>
internal static class LevelFactory
{
    public static void Build(BuildContext ctx)
    {
        var doc = ctx.Doc;
        var storeys = ctx.Model.Storeys.OrderBy(s => s.Index).ToList();
        if (storeys.Count == 0)
            throw new InvalidOperationException("The payload carries no storeys.");

        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .ToList();
        var taken = new HashSet<ElementId>();

        Level Ensure(double elevationFt, string name)
        {
            var hit = existing.FirstOrDefault(l =>
                !taken.Contains(l.Id) && Math.Abs(l.Elevation - elevationFt) <= 0.0165);
            if (hit is null)
            {
                hit = Level.Create(doc, elevationFt);
                existing.Add(hit);
            }
            taken.Add(hit.Id);
            TryRename(hit, name);
            return hit;
        }

        foreach (var s in storeys)
            ctx.Levels[s.Index] = Ensure(ImportGeometry.Ft(s.ElevationMm), s.Name ?? $"Level {s.Index + 1}");

        // The cap above the top storey. Named uniquely enough to never collide
        // with a real storey name coming from the app.
        var top = storeys[^1];
        ctx.TopLevel = Ensure(
            ImportGeometry.Ft(top.ElevationMm + Math.Max(1, top.HeightMm)),
            "Roof");

        // Template levels nothing claimed: delete, dependents (their views)
        // cascade. In a fresh template document nothing is hosted on them.
        var ghosts = existing.Where(l => !taken.Contains(l.Id)).Select(l => l.Id).ToList();
        if (ghosts.Count > 0)
        {
            try { doc.Delete(ghosts); }
            catch { /* a template with content on a level keeps it — non-fatal */ }
        }

        RebuildPlans(ctx);
        ctx.Count("Levels", ctx.Levels.Count + 1);
    }

    /// <summary>One canonical floor plan and ceiling plan per storey level —
    /// template leftovers dropped first so the browser isn't littered with
    /// "L1 - Architectural"-style views pointing at renamed levels.</summary>
    private static void RebuildPlans(BuildContext ctx)
    {
        var doc = ctx.Doc;
        var stale = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate
                        && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan))
            .Select(v => v.Id)
            .ToList();
        if (stale.Count > 0)
        {
            try { doc.Delete(stale); } catch { /* non-fatal */ }
        }

        var floorPlanType = FindViewFamilyType(doc, ViewFamily.FloorPlan);
        var ceilingPlanType = FindViewFamilyType(doc, ViewFamily.CeilingPlan);

        foreach (var level in ctx.Levels.Values.OrderBy(l => l.Elevation))
        {
            MakePlan(doc, floorPlanType, level, level.Name);
            MakePlan(doc, ceilingPlanType, level, $"Ceiling Plan - {level.Name}");
        }
    }

    private static void MakePlan(Document doc, ViewFamilyType? type, Level level, string name)
    {
        if (type is null) return;
        try
        {
            var plan = ViewPlan.Create(doc, type.Id, level.Id);
            TryRename(plan, name);
            try { plan.CropBoxActive = false; } catch { /* non-fatal */ }
        }
        catch { /* a template without this view family just goes without */ }
    }

    private static ViewFamilyType? FindViewFamilyType(Document doc, ViewFamily family)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == family);

    private static void TryRename(Element e, string name)
    {
        // Revit enforces unique names per class; a collision keeps the old one.
        try { e.Name = name; }
        catch
        {
            for (var i = 2; i < 100; i++)
            {
                try { e.Name = $"{name} ({i})"; return; }
                catch { /* keep probing */ }
            }
        }
    }
}
