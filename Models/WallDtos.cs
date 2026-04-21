using System.Text.Json.Serialization;

namespace RevitWallsPlugin.Models;

/// <summary>
/// Payload contract returned by the API export endpoint
/// (S:\api\src\projects\routes\exportProject.ts → buildProjectData).
///
/// Example:
///   {
///     "units": "mm",
///     "walls": [
///       { "type": "Generic", "level": "L1",
///         "start": [0, 0], "end": [5000, 0], "height": 3000 }
///     ]
///   }
/// </summary>
public sealed class WallsPayload
{
    /// <summary>Must be "mm". Any other value is rejected.</summary>
    [JsonPropertyName("units")]
    public string? Units { get; set; }

    [JsonPropertyName("walls")]
    public List<WallDto> Walls { get; set; } = new();
}

public sealed class WallDto
{
    /// <summary>Revit wall-type name. Falls back to project default if missing.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Level name (e.g. "L1"). Auto-created if missing.</summary>
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    /// <summary>[x, y] in millimetres.</summary>
    [JsonPropertyName("start")]
    public double[] Start { get; set; } = Array.Empty<double>();

    /// <summary>[x, y] in millimetres.</summary>
    [JsonPropertyName("end")]
    public double[] End { get; set; } = Array.Empty<double>();

    /// <summary>Wall height in millimetres.</summary>
    [JsonPropertyName("height")]
    public double Height { get; set; }
}
