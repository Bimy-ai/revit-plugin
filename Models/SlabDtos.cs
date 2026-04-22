namespace RevitWallsPlugin.Models;

/// <summary>
/// Base for horizontal-slab imports (floors, ceilings). A single DTO represents
/// one polygon with an outer ring and optional holes, expressed in millimetres
/// centered on the bounding-box midpoint of the whole import — same coordinate
/// convention as <see cref="WallDto"/>.
/// </summary>
public abstract class SlabDto
{
    public string Type { get; set; } = "Generic";

    /// <summary>Stable type identity (FloorTypeDto._id when available). Slabs
    /// sharing a TypeId resolve to the same Revit FloorType / CeilingType.</summary>
    public string? TypeId { get; set; }

    /// <summary>Base Revit level the slab is hosted on.</summary>
    public string Level { get; set; } = "Level 1";

    /// <summary>Slab structural-layer thickness in millimetres.</summary>
    public double ThicknessMm { get; set; } = 150;

    /// <summary>Hex RGB like "#a0c4ff" (no alpha). Null → default gray.</summary>
    public string? ColorHex { get; set; }

    /// <summary>Outer ring, [[x_mm, y_mm], ...]. At least 3 points.</summary>
    public double[][] Outer { get; set; } = Array.Empty<double[]>();

    /// <summary>Optional holes, each ring with at least 3 points.</summary>
    public double[][][] Holes { get; set; } = Array.Empty<double[][]>();
}

/// <summary>Floor slab emitted at the host level's elevation.</summary>
public sealed class FloorDto : SlabDto
{
}

/// <summary>
/// Ceiling emitted with a vertical offset above its host level — typically
/// the wall height of the story, so the ceiling lands just under the next
/// level's floor.
/// </summary>
public sealed class CeilingDto : SlabDto
{
    /// <summary>Vertical offset above the host level in millimetres.</summary>
    public double HeightOffsetMm { get; set; }
}
