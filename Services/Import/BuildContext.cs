using Autodesk.Revit.DB;
using BimyRevit.Models;

namespace BimyRevit.Services.Import;

/// <summary>
/// Everything the factory passes share while one payload is being built into
/// one document: the resolved levels, the material/type registries (payload id
/// → Revit element), the walls by payload id (openings host off them), and the
/// running counts + warnings the summary dialog reports.
/// </summary>
internal sealed class BuildContext
{
    public BuildContext(Document doc, RevitModel model, string importTag)
    {
        Doc = doc;
        Model = model;
        ImportTag = importTag;
    }

    public Document Doc { get; }
    public RevitModel Model { get; }

    /// <summary>"BIMy import &lt;projectId&gt;" — written into every created
    /// element's Comments so re-imports and audits can find them.</summary>
    public string ImportTag { get; }

    // ── Levels ──────────────────────────────────────────────────────────────
    public Dictionary<int, Level> Levels { get; } = new();
    /// <summary>The level above the top storey — the top constraint for
    /// top-storey walls and the roof's neighbour.</summary>
    public Level? TopLevel { get; set; }

    public Level LevelAt(int storey)
    {
        if (Levels.TryGetValue(storey, out var level)) return level;
        if (Levels.Count == 0) throw new InvalidOperationException("No levels were created.");
        var clamped = Math.Max(Levels.Keys.Min(), Math.Min(Levels.Keys.Max(), storey));
        return Levels[clamped];
    }

    /// <summary>The level whose elevation matches <paramref name="elevationFt"/>
    /// within 5 mm — including the extra top level — or null.</summary>
    public Level? LevelAtElevation(double elevationFt)
    {
        const double tol = 0.0165; // ≈ 5 mm in feet
        foreach (var l in Levels.Values)
            if (Math.Abs(l.Elevation - elevationFt) <= tol) return l;
        if (TopLevel is not null && Math.Abs(TopLevel.Elevation - elevationFt) <= tol) return TopLevel;
        return null;
    }

    // ── Registries ──────────────────────────────────────────────────────────
    /// <summary>Payload material id → Revit material. Missing ids answer
    /// InvalidElementId, which Revit renders as &lt;By Category&gt;.</summary>
    public Dictionary<string, ElementId> Materials { get; } = new(StringComparer.Ordinal);

    public ElementId MaterialId(string? payloadId)
        => payloadId is not null && Materials.TryGetValue(payloadId, out var id) ? id : ElementId.InvalidElementId;

    public Dictionary<string, WallType> WallTypes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FloorType> FloorTypes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CeilingType> CeilingTypes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, RoofType> RoofTypes { get; } = new(StringComparer.Ordinal);
    /// <summary>Door/window symbols by resolve key (type id, or a size key for
    /// untyped openings). A null value is remembered too: "no family on this
    /// machine" is an answer, and re-probing per door would re-log it per door.</summary>
    public Dictionary<string, FamilySymbol?> OpeningSymbols { get; } = new(StringComparer.Ordinal);

    /// <summary>Payload element-type id → its DTO, for the factories that
    /// resolve lazily.</summary>
    public Dictionary<string, ElementTypeDto> TypeDtos { get; } = new(StringComparer.Ordinal);

    // ── Built elements ──────────────────────────────────────────────────────
    public Dictionary<string, Wall> WallsById { get; } = new(StringComparer.Ordinal);

    // ── Reporting ───────────────────────────────────────────────────────────
    public Dictionary<string, int> Counts { get; } = new();
    public List<string> Warnings { get; } = new();

    public void Count(string label, int n = 1)
    {
        Counts.TryGetValue(label, out var cur);
        Counts[label] = cur + n;
    }

    public void Warn(string message)
    {
        Warnings.Add(message);
        Log.Warn("[native import] " + message);
    }

    /// <summary>Comments + Mark on a created element — the brand re-imports
    /// find elements by, and the payload guid for traceability.</summary>
    public void Brand(Element e, string? guid, string? name = null)
    {
        ParamSet.TrySet(e, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
            guid is null ? ImportTag : $"{ImportTag} · {guid}");
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { e.Name = name!; } catch { /* name rules vary by class — non-fatal */ }
        }
    }
}
