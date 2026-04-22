namespace RevitWallsPlugin.Models;

/// <summary>
/// Internal wall representation produced by <see cref="Services.ProjectBuilder"/>
/// and consumed by <see cref="Services.WallBuilder"/>.
///
/// Coordinates and height are in millimetres, centered so that the
/// bounding-box midpoint lands at (0, 0). Ring winding is normalized so that
/// with Revit's default flip=false and Location Line = Finish Face Exterior,
/// the wall's interior side faces the building interior.
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
}
