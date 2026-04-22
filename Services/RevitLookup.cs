using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

internal static class RevitLookup
{
    private const double DefaultFloorHeightMm = 3000;

    /// <summary>
    /// Ensures levels exist for every name referenced by the import. Levels listed
    /// in <paramref name="elevationsMm"/> are placed (or re-placed) at their exact
    /// computed elevation; other referenced names fall back to stacked defaults.
    /// Must be called inside an open transaction.
    /// </summary>
    public static (Dictionary<string, Level> All, HashSet<string> Created)
        EnsureLevels(
            Document doc,
            IEnumerable<string> referencedNames,
            Dictionary<string, double> elevationsMm)
    {
        var byName = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .ToDictionary(l => l.Name, l => l, StringComparer.OrdinalIgnoreCase);

        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultFloorFeet = UnitUtils.ConvertToInternalUnits(DefaultFloorHeightMm, UnitTypeId.Millimeters);

        var floorPlanType = FindViewFamilyType(doc, ViewFamily.FloorPlan);
        var ceilingPlanType = FindViewFamilyType(doc, ViewFamily.CeilingPlan);

        foreach (var kvp in elevationsMm.OrderBy(kvp => kvp.Value))
        {
            var name = kvp.Key;
            var elevationFeet = UnitUtils.ConvertToInternalUnits(kvp.Value, UnitTypeId.Millimeters);

            if (byName.TryGetValue(name, out var existing))
            {
                if (Regex.IsMatch(name, @"^Level \d+$", RegexOptions.IgnoreCase))
                {
                    try { existing.Elevation = elevationFeet; } catch { /* ignore */ }
                }
                continue;
            }

            CreateLevel(doc, name, elevationFeet, byName, created);
        }

        foreach (var raw in referencedNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            if (byName.ContainsKey(name)) continue;

            var maxElev = byName.Count > 0 ? byName.Values.Max(l => l.Elevation) : 0;
            CreateLevel(doc, name, maxElev + defaultFloorFeet, byName, created);
        }

        DeleteGhostLevels(doc, byName, elevationsMm, referencedNames);

        // Produce exactly one "Plan - <levelName>" floor plan and one
        // "Ceiling Plan - <levelName>" ceiling plan per level — for both the
        // levels we just created AND any existing template levels we reused.
        // Callers are expected to have wiped stale floor/ceiling plans via
        // DeleteAllFloorPlans / DeleteAllCeilingPlans before this point so
        // the template's "L1 - Architectural"-style views don't linger.
        foreach (var level in byName.Values.ToList())
        {
            EnsureFloorPlan(doc, level, floorPlanType);
            EnsureCeilingPlan(doc, level, ceilingPlanType);
        }

        return (byName, created);
    }

    // Remove template-leftover levels (e.g. the default "L1"/"L2" that ship in
    // empty projects) so they don't compete with our imported ones. A level is
    // considered a ghost when it's not referenced by the import AND nothing in
    // the document is hosted on it. Associated views cascade-delete with it.
    private static void DeleteGhostLevels(
        Document doc,
        Dictionary<string, Level> byName,
        Dictionary<string, double> elevationsMm,
        IEnumerable<string> referencedNames)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in elevationsMm.Keys) wanted.Add(n);
        foreach (var raw in referencedNames)
        {
            if (!string.IsNullOrWhiteSpace(raw)) wanted.Add(raw.Trim());
        }

        var ghostIds = new List<ElementId>();
        foreach (var kvp in byName.ToList())
        {
            if (wanted.Contains(kvp.Key)) continue;

            var level = kvp.Value;
            // Views (floor plans, ceiling plans, RCPs…) that reference this
            // level also come back from ElementLevelFilter, but they're not
            // "real" dependents — they cascade-delete with the level. Only
            // model elements should block deletion.
            var hasModelDependents = new FilteredElementCollector(doc)
                .WherePasses(new ElementLevelFilter(level.Id))
                .Cast<Element>()
                .Any(e => e is not View);
            if (hasModelDependents) continue;

            ghostIds.Add(level.Id);
            byName.Remove(kvp.Key);
        }

        if (ghostIds.Count == 0) return;

        try { doc.Delete(ghostIds); }
        catch { /* non-fatal: if Revit refuses (e.g. last-level rule), leave them */ }
    }

    private static void CreateLevel(
        Document doc,
        string name,
        double elevationFeet,
        Dictionary<string, Level> byName,
        HashSet<string> created)
    {
        var level = Level.Create(doc, elevationFeet);
        try { level.Name = name; } catch { /* duplicate/invalid name — leave as generated */ }

        byName[level.Name] = level;
        created.Add(level.Name);
    }

    // Make sure exactly one floor-plan view exists for the level and is named
    // "Plan - <levelName>". Any existing floor plans for this level get renamed;
    // if there are none, one is created from the given ViewFamilyType.
    private static void EnsureFloorPlan(Document doc, Level level, ViewFamilyType? floorPlanType)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate
                        && v.ViewType == ViewType.FloorPlan
                        && v.GenLevel != null
                        && v.GenLevel.Id == level.Id)
            .ToList();

        foreach (var plan in existing)
            TryRenameView(plan, $"Plan - {level.Name}");

        if (existing.Count > 0 || floorPlanType is null) return;

        try
        {
            var plan = ViewPlan.Create(doc, floorPlanType.Id, level.Id);
            TryRenameView(plan, $"Plan - {level.Name}");
        }
        catch { /* non-fatal */ }
    }

    // Same idea as EnsureFloorPlan, for ceiling plans.
    private static void EnsureCeilingPlan(Document doc, Level level, ViewFamilyType? ceilingPlanType)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate
                        && v.ViewType == ViewType.CeilingPlan
                        && v.GenLevel != null
                        && v.GenLevel.Id == level.Id)
            .ToList();

        foreach (var plan in existing)
            TryRenameView(plan, $"Ceiling Plan - {level.Name}");

        if (existing.Count > 0 || ceilingPlanType is null) return;

        try
        {
            var plan = ViewPlan.Create(doc, ceilingPlanType.Id, level.Id);
            TryRenameView(plan, $"Ceiling Plan - {level.Name}");
        }
        catch { /* non-fatal */ }
    }

    private static ViewFamilyType? FindViewFamilyType(Document doc, ViewFamily family)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == family);

    /// <summary>
    /// Deletes every non-template ceiling-plan view in the document. Intended
    /// to clear stale, template-generated ceiling plans before a load so new
    /// ones can be created fresh per level. The active view is skipped — Revit
    /// won't let us delete it, and trying would abort the whole batch.
    /// </summary>
    public static int DeleteAllCeilingPlans(Document doc)
        => DeletePlansOfType(doc, ViewType.CeilingPlan);

    /// <summary>
    /// Deletes every non-template floor-plan view in the document. Paired with
    /// <see cref="EnsureFloorPlan"/> so callers can produce a canonical
    /// "Plan - &lt;level&gt;" per level without leftover template views (e.g.
    /// "L1 - Architectural") polluting the Project Browser.
    /// </summary>
    public static int DeleteAllFloorPlans(Document doc)
        => DeletePlansOfType(doc, ViewType.FloorPlan);

    private static int DeletePlansOfType(Document doc, ViewType viewType)
    {
        var activeId = doc.ActiveView?.Id;
        var ids = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate
                        && v.ViewType == viewType
                        && (activeId is null || v.Id != activeId))
            .Select(v => v.Id)
            .ToList();

        if (ids.Count == 0) return 0;

        try
        {
            var deleted = doc.Delete(ids);
            return deleted?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryRenameView(View view, string desiredName)
    {
        // Revit requires unique view names within a doc — fall back silently on
        // collision rather than failing the whole import.
        try { view.Name = desiredName; }
        catch { /* keep Revit's auto-generated name */ }
    }

    public static Level ResolveLevel(Dictionary<string, Level> byName, string name)
    {
        if (byName.TryGetValue(name, out var exact))
            return exact;

        var lowest = byName.Values.OrderBy(l => l.Elevation).FirstOrDefault()
                     ?? throw new InvalidOperationException("No levels exist in the project.");
        return lowest;
    }
}
