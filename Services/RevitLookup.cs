using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

internal static class RevitLookup
{
    private const double DefaultFloorHeightMm = 3000;

    public static WallType FindWallTypeOrDefault(Document doc, string name, out bool wasFallback)
    {
        var exact = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(wt => string.Equals(wt.Name, name, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            wasFallback = false;
            return exact;
        }

        var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
        if (defaultId != ElementId.InvalidElementId && doc.GetElement(defaultId) is WallType def)
        {
            wasFallback = true;
            return def;
        }

        var any = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault();

        if (any is null)
            throw new InvalidOperationException("No wall types exist in the project.");

        wasFallback = true;
        return any;
    }

    /// <summary>
    /// Ensures a level with each of <paramref name="names"/> exists in the document,
    /// creating missing ones at elevations inferred from names like "L3" (→ floor 3).
    /// Must be called inside an open transaction.
    /// </summary>
    public static (Dictionary<string, Level> All, HashSet<string> Created)
        EnsureLevels(Document doc, IEnumerable<string> names)
    {
        var byName = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .ToDictionary(l => l.Name, l => l, StringComparer.OrdinalIgnoreCase);

        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var floorHeightFeet = UnitUtils.ConvertToInternalUnits(DefaultFloorHeightMm, UnitTypeId.Millimeters);

        var floorPlanType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);

        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            if (byName.ContainsKey(name)) continue;

            double elevationFeet;
            var match = Regex.Match(name, @"\d+");
            if (match.Success && int.TryParse(match.Value, out var floorNumber) && floorNumber > 0)
            {
                elevationFeet = (floorNumber - 1) * floorHeightFeet;
            }
            else
            {
                var maxElev = byName.Count > 0 ? byName.Values.Max(l => l.Elevation) : 0;
                elevationFeet = maxElev + floorHeightFeet;
            }

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

        return (byName, created);
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
