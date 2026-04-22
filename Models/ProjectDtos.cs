using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitWallsPlugin.Models;

/// <summary>
/// Contract returned by the API export endpoint:
///   GET /api/:tenantId/projects/:projectId/export
/// → { "userObjects": [...] }
///
/// The plug-in is responsible for converting userObjects into walls
/// (unit conversion, polygon normalization, bounding-box centering).
/// </summary>
public sealed class UserObjectsPayload
{
    [JsonPropertyName("userObjects")]
    public List<UserObjectDto>? UserObjects { get; set; }
}

public sealed class UserObjectDto
{
    /// <summary>Per-floor indices into <see cref="Types"/>. Default [0] if missing.</summary>
    [JsonPropertyName("floors")]
    public List<int>? Floors { get; set; }

    [JsonPropertyName("types")]
    public List<FloorTypeDto>? Types { get; set; }

    /// <summary>
    /// Polygon data. Either a flat ring [{x,y},...] (legacy) or an array of
    /// polygons where each entry is a ring or [outer, hole, hole…].
    /// </summary>
    [JsonPropertyName("polygonPoints")]
    public JsonElement PolygonPoints { get; set; }
}

public sealed class FloorTypeDto
{
    /// <summary>Stable identity used to link walls/floors/ceilings across stories.
    /// Two types with the same Id share Revit WallType/FloorType/CeilingType.</summary>
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    /// <summary>Position of the type within its parent UserObject. Mirrors the
    /// indices used in <see cref="UserObjectDto.Floors"/>.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Height in meters.</summary>
    [JsonPropertyName("height")]
    public double? Height { get; set; }

    /// <summary>Wall thickness in meters. Falls back to 0.2 m (200 mm) when absent.</summary>
    [JsonPropertyName("thickness")]
    public double? Thickness { get; set; }

    /// <summary>Hex RGB like "#a0c4ff". Applied as the wall material color.</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>Per-type wall polygon data. Same shape as UserObject.PolygonPoints.</summary>
    [JsonPropertyName("walls")]
    public JsonElement Walls { get; set; }

    /// <summary>Per-type floor-slab polygon data. Same shape as UserObject.PolygonPoints.</summary>
    [JsonPropertyName("floor")]
    public JsonElement Floor { get; set; }

    /// <summary>Per-type ceiling polygon data. Same shape as UserObject.PolygonPoints.</summary>
    [JsonPropertyName("ceiling")]
    public JsonElement Ceiling { get; set; }
}
