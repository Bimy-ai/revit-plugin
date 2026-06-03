namespace RevitWallsPlugin.Models;

/// <summary>
/// Internal wall representation produced by <see cref="Services.ProjectBuilder"/>
/// and consumed by <see cref="Services.WallBuilder"/>.
///
/// Coordinates and height are in millimetres, centered so that the
/// bounding-box midpoint lands at (0, 0). One DTO per wall instance —
/// segments from the editor's DSL pass through one-for-one; the legacy
/// polygon path emits one DTO per polygon edge with <see cref="Baseline"/>
/// forced to +1 to match its pre-normalised winding.
/// </summary>
public sealed class WallDto
{
    public string Type { get; set; } = "Generic";

    /// <summary>Stable type identity (FloorTypeDto._id when available). Walls
    /// sharing a TypeId resolve to the same Revit WallType.</summary>
    public string? TypeId { get; set; }

    public string Level { get; set; } = "Level 1";

    /// <summary>Optional explicit top-constraint level. When set, the wall
    /// spans from <see cref="Level"/> up to this level (used to coalesce
    /// runs of consecutive same-type stories into one wall instance).
    /// When null, WallBuilder picks the next level above by elevation.</summary>
    public string? TopLevel { get; set; }

    public double[] Start { get; set; } = Array.Empty<double>();
    public double[] End { get; set; } = Array.Empty<double>();
    public double Height { get; set; }

    /// <summary>Wall structural-layer thickness in millimetres.</summary>
    public double ThicknessMm { get; set; } = 200;

    /// <summary>Hex RGB like "#a0c4ff" (no alpha). Null → default gray.</summary>
    public string? ColorHex { get; set; }

    /// <summary>
    /// Where the wall body sits relative to its location line (start→end), from
    /// the BIMy editor's segment DSL:
    ///   −1 → body on the −normal side (Revit exterior side, flip=false)
    ///    0 → centered
    ///   +1 → body on the +normal side (Revit interior side, flip=false)
    /// The editor's normal is `(dir.y, −dir.x)` — right of direction — which
    /// equals Revit's interior side when the wall is created with flip=false.
    /// WallBuilder maps this to <c>WALL_KEY_REF_PARAM</c>.
    /// </summary>
    public int Baseline { get; set; } = 0;

    /// <summary>Optional classifier from the new DSL: "structural" or
    /// "partition". Threaded through for future use (e.g. structural usage,
    /// function on the type); unset on legacy / polygon-derived walls.</summary>
    public string? Kind { get; set; }
}
