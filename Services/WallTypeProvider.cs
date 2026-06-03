using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Resolves the Revit <see cref="WallType"/> for an imported wall. A single
/// fallback "Concrete 12\"" type is found at construction time; per-thickness
/// variants are duplicated from it on demand and cached so a project with N
/// distinct wall depths produces N wall types (one per ≈5mm bucket), not one
/// per instance.
/// Imported walls stay branded on the instance via <see cref="ImportedTag"/>
/// so re-imports can find and replace them.
/// Must be used inside an open transaction.
/// </summary>
internal sealed class WallTypeProvider
{
    public const string ImportedTag = "BIMy import";
    public const string TypeNamePrefix = "BIMy Wall";

    // 12 inches in feet — the width we prefer for the fallback search when no
    // wall type with an exact matching name is present.
    private const double PreferredWidthFeet = 1.0;
    private const double WidthToleranceFeet = 0.05; // ±0.6"

    // Bucket per-wall thickness to the nearest 5mm. A scanned plan can quote
    // wall depths to four decimal metres, so without bucketing nearly every
    // wall ends up with its own duplicated type; 5mm is tighter than any
    // construction tolerance and keeps "200" and "201" mm walls on the same
    // type instead of producing 100 near-identical ones.
    private const int ThicknessBucketMm = 5;

    private readonly Document _doc;
    private readonly WallType _fallback;
    private readonly Dictionary<int, WallType> _byBucket = new();

    public HashSet<string> CreatedWallTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public WallTypeProvider(Document doc, ImportMaterialProvider materials)
    {
        _ = materials; // retained for API symmetry; the import material isn't
                       // currently applied to wall types.
        _doc = doc;
        _fallback = FindConcrete12InchType(doc)
            ?? throw new InvalidOperationException("No basic wall type exists in the project.");
    }

    /// <summary>
    /// Returns a wall type whose structural-layer width matches
    /// <paramref name="thicknessMm"/> within the bucket tolerance. The first
    /// call for a bucket clones the fallback type; subsequent calls reuse it.
    /// </summary>
    public WallType Get(double thicknessMm)
    {
        var bucket = ThicknessBucket(thicknessMm);
        if (_byBucket.TryGetValue(bucket, out var cached)) return cached;

        var widthFeet = MmToFeet(bucket);

        // If the fallback already matches the bucket within tolerance, reuse it
        // unmodified — no point duplicating "Concrete - 12\"" to itself.
        if (Math.Abs(_fallback.Width - widthFeet) <= WidthToleranceFeet)
        {
            _byBucket[bucket] = _fallback;
            return _fallback;
        }

        var name = UniqueName($"{TypeNamePrefix} {bucket} mm");
        var clone = (WallType)_fallback.Duplicate(name);
        TrySetStructuralLayerWidth(clone, widthFeet);
        CreatedWallTypes.Add(name);
        _byBucket[bucket] = clone;
        return clone;
    }

    /// <summary>Back-compat for callers that don't carry a thickness — uses the
    /// fallback type unchanged.</summary>
    public WallType Get() => _fallback;

    private static int ThicknessBucket(double thicknessMm)
    {
        if (!(thicknessMm > 0)) thicknessMm = 200;
        var bucket = (int)Math.Round(thicknessMm / ThicknessBucketMm) * ThicknessBucketMm;
        return Math.Max(ThicknessBucketMm, bucket);
    }

    // Replace the structural-layer width on the cloned type. We try the core
    // layer first; if the type has no core layer marker (some templates), fall
    // back to the widest existing layer — that's almost always the structural
    // one in a single-layer basic wall.
    private static void TrySetStructuralLayerWidth(WallType wt, double widthFeet)
    {
        var cs = wt.GetCompoundStructure();
        if (cs is null) return;

        var layerIdx = cs.GetFirstCoreLayerIndex();
        if (layerIdx < 0 || layerIdx >= cs.LayerCount)
            layerIdx = WidestLayerIndex(cs);
        if (layerIdx < 0) return;

        try
        {
            cs.SetLayerWidth(layerIdx, widthFeet);
            wt.SetCompoundStructure(cs);
        }
        catch
        {
            // Some templates lock layer widths (e.g. via family-template
            // constraints). Skipping is non-fatal: the wall still imports with
            // the fallback thickness, just not the editor's.
        }
    }

    private static int WidestLayerIndex(CompoundStructure cs)
    {
        var best = -1;
        var bestWidth = -1.0;
        for (var i = 0; i < cs.LayerCount; i++)
        {
            var w = cs.GetLayerWidth(i);
            if (w > bestWidth) { bestWidth = w; best = i; }
        }
        return best;
    }

    private string UniqueName(string baseName)
    {
        // Existing project may already have a wall type with this name (e.g.
        // from a prior import that wasn't fully cleaned up). Append a numeric
        // suffix until we find a free slot rather than throwing on Duplicate.
        var existing = new FilteredElementCollector(_doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
        return $"{baseName} ({Guid.NewGuid():N})"; // pathological fallback
    }

    private static double MmToFeet(double mm)
        => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    // Preference order:
    //   1. Common US-imperial template names for the 12" concrete wall.
    //   2. Any basic wall type whose name contains "Concrete" and whose
    //      structural width is ≈ 12".
    //   3. The project's default wall type (whatever the user was working
    //      with before the import).
    //   4. Any basic wall type at all — last-resort so the import can still
    //      produce walls on templates that have no concrete types.
    private static WallType? FindConcrete12InchType(Document doc)
    {
        var basicWalls = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .Where(wt => wt.Kind == WallKind.Basic)
            .ToList();

        foreach (var name in new[]
                 {
                     "Concrete - 12\" Concrete",
                     "Concrete - 12\"",
                     "Basic Wall: Concrete - 12\" Concrete",
                     "Concrete 12\"",
                 })
        {
            var hit = basicWalls.FirstOrDefault(wt =>
                string.Equals(wt.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        var widthMatch = basicWalls
            .Where(wt => wt.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault(wt => Math.Abs(wt.Width - PreferredWidthFeet) <= WidthToleranceFeet);
        if (widthMatch is not null) return widthMatch;

        var defaultId = doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
        if (defaultId != ElementId.InvalidElementId
            && doc.GetElement(defaultId) is WallType def
            && def.Kind == WallKind.Basic)
        {
            return def;
        }

        return basicWalls.FirstOrDefault();
    }
}
