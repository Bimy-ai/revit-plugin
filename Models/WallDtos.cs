namespace RevitWallsPlugin.Models;

/// <summary>
/// Internal wall representation produced by <see cref="Services.ProjectBuilder"/>
/// and consumed by <see cref="Services.WallBuilder"/>.
///
/// Coordinates and height are in millimetres, centered so that the
/// bounding-box midpoint lands at (0, 0).
/// </summary>
public sealed class WallDto
{
    public string Type { get; set; } = "Generic";
    public string Level { get; set; } = "L1";
    public double[] Start { get; set; } = Array.Empty<double>();
    public double[] End { get; set; } = Array.Empty<double>();
    public double Height { get; set; }

    /// <summary>Wall structural-layer thickness in millimetres.</summary>
    public double ThicknessMm { get; set; } = 200;

    /// <summary>Hex RGB like "#a0c4ff" (no alpha). Null → default gray.</summary>
    public string? ColorHex { get; set; }
}
