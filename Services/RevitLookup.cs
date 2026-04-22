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

        var floorPlanType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);

        foreach (var kvp in elevationsMm.OrderBy(kvp => kvp.Value))
        {
            var name = kvp.Key;
            var elevationFeet = UnitUtils.ConvertToInternalUnits(kvp.Value, UnitTypeId.Millimeters);

            if (byName.TryGetValue(name, out var existing))
            {
                if (Regex.IsMatch(name, @"^L\d+$", RegexOptions.IgnoreCase))
                {
                    try { existing.Elevation = elevationFeet; } catch { /* ignore */ }
                }
                continue;
            }

            CreateLevel(doc, name, elevationFeet, floorPlanType, byName, created);
        }

        foreach (var raw in referencedNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            if (byName.ContainsKey(name)) continue;

            var maxElev = byName.Count > 0 ? byName.Values.Max(l => l.Elevation) : 0;
            CreateLevel(doc, name, maxElev + defaultFloorFeet, floorPlanType, byName, created);
        }

        return (byName, created);
    }

    private static void CreateLevel(
        Document doc,
        string name,
        double elevationFeet,
        ViewFamilyType? floorPlanType,
        Dictionary<string, Level> byName,
        HashSet<string> created)
    {
        var level = Level.Create(doc, elevationFeet);
        try { level.Name = name; } catch { /* duplicate/invalid name — leave as generated */ }

        if (floorPlanType is not null)
        {
            try { ViewPlan.Create(doc, floorPlanType.Id, level.Id); }
            catch { /* non-fatal: user can still create the plan manually */ }
        }

        byName[level.Name] = level;
        created.Add(level.Name);
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
