using System.Text.Json;
using System.Text.Json.Serialization;

namespace BimyRevit.Models;

/// <summary>
/// The `bimy-revit-model/1` payload — the parametric building the API serves at
/// <c>GET /api/export/revit-model/:projectId</c> for DRAWN projects, written by
/// the app's own generator (the same enumeration that writes the IFC export).
/// Lengths are integer millimetres, angles degrees; plan points are
/// <c>[x, y]</c> with +y = north, which is Revit's own plan frame, so values
/// convert by unit only. See bim-frontend/src/lib/ifc/revitModel.js — the
/// writer — for the field-by-field contract.
/// </summary>
public sealed class RevitModel
{
    public const string ExpectedSchema = "bimy-revit-model/1";

    [JsonPropertyName("schema")] public string? Schema { get; set; }
    /// <summary>Set instead of everything else when the project's model is an
    /// imported IFC — the plugin must pull <c>/revit-ifc</c> instead.</summary>
    [JsonPropertyName("fallback")] public string? Fallback { get; set; }

    [JsonPropertyName("project")] public ProjectInfoDto Project { get; set; } = new();
    [JsonPropertyName("generatedAt")] public string? GeneratedAt { get; set; }
    [JsonPropertyName("location")] public LocationDto? Location { get; set; }
    [JsonPropertyName("storeys")] public List<StoreyDto> Storeys { get; set; } = new();
    [JsonPropertyName("grids")] public List<GridAxisDto> Grids { get; set; } = new();
    [JsonPropertyName("materials")] public List<MaterialDto> Materials { get; set; } = new();
    [JsonPropertyName("elementTypes")] public List<ElementTypeDto> ElementTypes { get; set; } = new();
    [JsonPropertyName("walls")] public List<WallDto> Walls { get; set; } = new();
    [JsonPropertyName("openings")] public List<OpeningDto> Openings { get; set; } = new();
    [JsonPropertyName("floors")] public List<SlabDto> Floors { get; set; } = new();
    [JsonPropertyName("ceilings")] public List<SlabDto> Ceilings { get; set; } = new();
    [JsonPropertyName("roofs")] public List<RoofDto> Roofs { get; set; } = new();
    [JsonPropertyName("solidElements")] public List<SolidElementDto> SolidElements { get; set; } = new();
    [JsonPropertyName("furnitureTypes")] public List<FurnitureTypeDto> FurnitureTypes { get; set; } = new();
    [JsonPropertyName("furniture")] public List<FurnitureDto> Furniture { get; set; } = new();
    [JsonPropertyName("rooms")] public List<RoomDto> Rooms { get; set; } = new();
    [JsonPropertyName("unhandled")] public List<UnhandledDto> Unhandled { get; set; } = new();
    [JsonPropertyName("stats")] public Dictionary<string, int> Stats { get; set; } = new();

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static RevitModel? Parse(string json) => JsonSerializer.Deserialize<RevitModel>(json, _json);
}

public sealed class ProjectInfoDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class LocationDto
{
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }
}

public sealed class StoreyDto
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("elevationMm")] public double ElevationMm { get; set; }
    [JsonPropertyName("heightMm")] public double HeightMm { get; set; }
}

public sealed class GridAxisDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("a")] public double[] A { get; set; } = new double[2];
    [JsonPropertyName("b")] public double[] B { get; set; } = new double[2];
}

public sealed class MaterialDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    /// <summary>Already the Revit TEMPLATE material name (the app bridges its
    /// canonical vocabulary) — create verbatim so it folds into user templates.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("transparency")] public int Transparency { get; set; }
}

public sealed class ElementTypeDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    /// <summary>wall | floor | ceiling | roof | door | window | …</summary>
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }

    // Construction categories — the layer build-up, outermost first.
    [JsonPropertyName("layers")] public List<TypeLayerDto>? Layers { get; set; }
    [JsonPropertyName("totalThicknessMm")] public double TotalThicknessMm { get; set; }

    // Opening categories — the size IS the type.
    [JsonPropertyName("widthMm")] public double WidthMm { get; set; }
    [JsonPropertyName("heightMm")] public double HeightMm { get; set; }
    [JsonPropertyName("sillMm")] public double SillMm { get; set; }
    [JsonPropertyName("unfilled")] public bool Unfilled { get; set; }
    [JsonPropertyName("operation")] public string? Operation { get; set; }
}

public sealed class TypeLayerDto
{
    /// <summary>Structure | Insulation | Finish | Membrane | Air gap | Substrate.</summary>
    [JsonPropertyName("function")] public string? Function { get; set; }
    [JsonPropertyName("materialId")] public string? MaterialId { get; set; }
    [JsonPropertyName("thicknessMm")] public double ThicknessMm { get; set; }
}

public sealed class WallDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("storey")] public int Storey { get; set; }
    [JsonPropertyName("a")] public double[] A { get; set; } = new double[2];
    [JsonPropertyName("b")] public double[] B { get; set; } = new double[2];
    [JsonPropertyName("arc")] public WallArcDto? Arc { get; set; }
    [JsonPropertyName("thicknessMm")] public double ThicknessMm { get; set; }
    [JsonPropertyName("heightMm")] public double HeightMm { get; set; }
    [JsonPropertyName("baseOffsetMm")] public double BaseOffsetMm { get; set; }
    /// <summary>−1 | 0 | +1 — which line of the wall the user drew (interior
    /// face / centre / exterior face). Maps to Revit's Location Line.</summary>
    [JsonPropertyName("baseline")] public int Baseline { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("typeId")] public string? TypeId { get; set; }
    [JsonPropertyName("materialId")] public string? MaterialId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("guid")] public string? Guid { get; set; }
}

public sealed class WallArcDto
{
    [JsonPropertyName("center")] public double[] Center { get; set; } = new double[2];
    [JsonPropertyName("rMm")] public double RMm { get; set; }
    [JsonPropertyName("ccw")] public bool Ccw { get; set; }
}

public sealed class OpeningDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("hostWallId")] public string HostWallId { get; set; } = string.Empty;
    [JsonPropertyName("storey")] public int Storey { get; set; }
    /// <summary>door | window | opening (a plain hole).</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("unfilled")] public bool Unfilled { get; set; }
    /// <summary>World plan point of the opening's centre — host the family
    /// instance here and Revit projects it onto the wall, arcs included.</summary>
    [JsonPropertyName("center")] public double[] Center { get; set; } = new double[2];
    [JsonPropertyName("widthMm")] public double WidthMm { get; set; }
    [JsonPropertyName("heightMm")] public double HeightMm { get; set; }
    [JsonPropertyName("sillMm")] public double SillMm { get; set; }
    [JsonPropertyName("flipHand")] public bool FlipHand { get; set; }
    [JsonPropertyName("flipFacing")] public bool FlipFacing { get; set; }
    [JsonPropertyName("operation")] public string? Operation { get; set; }
    [JsonPropertyName("archRadiusMm")] public double ArchRadiusMm { get; set; }
    [JsonPropertyName("typeId")] public string? TypeId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("guid")] public string? Guid { get; set; }
}

public sealed class SlabDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("storey")] public int Storey { get; set; }
    [JsonPropertyName("ring")] public List<double[]> Ring { get; set; } = new();
    [JsonPropertyName("holes")] public List<List<double[]>> Holes { get; set; } = new();
    /// <summary>Height of the slab's UNDERSIDE above the storey level.</summary>
    [JsonPropertyName("zOffsetMm")] public double ZOffsetMm { get; set; }
    [JsonPropertyName("thicknessMm")] public double ThicknessMm { get; set; }
    [JsonPropertyName("slope")] public SlabSlopeDto? Slope { get; set; }
    [JsonPropertyName("typeId")] public string? TypeId { get; set; }
    [JsonPropertyName("materialId")] public string? MaterialId { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("guid")] public string? Guid { get; set; }
}

public sealed class SlabSlopeDto
{
    [JsonPropertyName("refMm")] public double[] RefMm { get; set; } = new double[2];
    /// <summary>Unit uphill direction in plan.</summary>
    [JsonPropertyName("uphill")] public double[] Uphill { get; set; } = new double[2];
    /// <summary>Rise per unit run (the tangent).</summary>
    [JsonPropertyName("pitch")] public double Pitch { get; set; }
}

public sealed class RoofDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("storey")] public int Storey { get; set; }
    /// <summary>flat | shed | gable | hip.</summary>
    [JsonPropertyName("shape")] public string? Shape { get; set; }
    [JsonPropertyName("ring")] public List<double[]> Ring { get; set; } = new();
    [JsonPropertyName("holes")] public List<List<double[]>> Holes { get; set; } = new();
    [JsonPropertyName("pitchDeg")] public double PitchDeg { get; set; }
    [JsonPropertyName("overhangMm")] public double OverhangMm { get; set; }
    [JsonPropertyName("thicknessMm")] public double ThicknessMm { get; set; }
    /// <summary>The eave plane above the storey level (the top of its walls).</summary>
    [JsonPropertyName("zOffsetMm")] public double ZOffsetMm { get; set; }
    /// <summary>Per outer-ring edge: does a footprint roof slope from it.</summary>
    [JsonPropertyName("slopedEdges")] public List<bool>? SlopedEdges { get; set; }
    [JsonPropertyName("nativeEligible")] public bool NativeEligible { get; set; }
    /// <summary>The exact triangles the app renders — the faithful fallback.</summary>
    [JsonPropertyName("mesh")] public MeshDto? Mesh { get; set; }
    [JsonPropertyName("typeId")] public string? TypeId { get; set; }
    [JsonPropertyName("materialId")] public string? MaterialId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("guid")] public string? Guid { get; set; }
}

public sealed class MeshDto
{
    /// <summary>Flat x,y,z triples, millimetres, Z-up.</summary>
    [JsonPropertyName("positions")] public List<double> Positions { get; set; } = new();
    [JsonPropertyName("indices")] public List<int> Indices { get; set; } = new();
}

public sealed class SolidElementDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("storey")] public int Storey { get; set; }
    /// <summary>stair | ramp | column | foundation | pile | railing | …</summary>
    [JsonPropertyName("category")] public string? Category { get; set; }
    /// <summary>Set for placed families (stairs…): the local frame's plan
    /// anchor; extrusion rings are then LOCAL to it. Absent for world-frame
    /// solids (foundations, railings).</summary>
    [JsonPropertyName("at")] public double[]? At { get; set; }
    [JsonPropertyName("zOffsetMm")] public double ZOffsetMm { get; set; }
    [JsonPropertyName("rotationDeg")] public double RotationDeg { get; set; }
    [JsonPropertyName("extrusions")] public List<ExtrusionDto> Extrusions { get; set; } = new();
    [JsonPropertyName("cylinders")] public List<CylinderDto> Cylinders { get; set; } = new();
    [JsonPropertyName("materialId")] public string? MaterialId { get; set; }
    [JsonPropertyName("derived")] public Dictionary<string, JsonElement>? Derived { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("guid")] public string? Guid { get; set; }
}

public sealed class ExtrusionDto
{
    [JsonPropertyName("ring")] public List<double[]> Ring { get; set; } = new();
    [JsonPropertyName("holes")] public List<List<double[]>> Holes { get; set; } = new();
    [JsonPropertyName("z0Mm")] public double Z0Mm { get; set; }
    [JsonPropertyName("dzMm")] public double DzMm { get; set; }
}

public sealed class CylinderDto
{
    [JsonPropertyName("center")] public double[] Center { get; set; } = new double[2];
    [JsonPropertyName("rMm")] public double RMm { get; set; }
    [JsonPropertyName("z0Mm")] public double Z0Mm { get; set; }
    [JsonPropertyName("dzMm")] public double DzMm { get; set; }
}

public sealed class FurnitureTypeDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("parts")] public List<FurnitureMeshPartDto> Parts { get; set; } = new();
    [JsonPropertyName("box")] public FurnitureBoxDto? Box { get; set; }
}

public sealed class FurnitureMeshPartDto
{
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1;
    [JsonPropertyName("positions")] public List<double> Positions { get; set; } = new();
    [JsonPropertyName("indices")] public List<int> Indices { get; set; } = new();
}

public sealed class FurnitureBoxDto
{
    [JsonPropertyName("wMm")] public double WMm { get; set; }
    [JsonPropertyName("dMm")] public double DMm { get; set; }
    [JsonPropertyName("hMm")] public double HMm { get; set; }
}

public sealed class FurnitureDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("typeKey")] public string TypeKey { get; set; } = string.Empty;
    [JsonPropertyName("storey")] public int Storey { get; set; }
    [JsonPropertyName("at")] public double[] At { get; set; } = new double[2];
    [JsonPropertyName("zOffsetMm")] public double ZOffsetMm { get; set; }
    /// <summary>The full placement frame as two unit vectors (Z-up payload
    /// coords) — no angle conventions to guess.</summary>
    [JsonPropertyName("up")] public double[] Up { get; set; } = { 0, 0, 1 };
    [JsonPropertyName("right")] public double[] Right { get; set; } = { 1, 0, 0 };
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("guid")] public string? Guid { get; set; }
}

public sealed class RoomDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("storey")] public int Storey { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }
    /// <summary>A point strictly inside the room — where the Room lands.</summary>
    [JsonPropertyName("point")] public double[] Point { get; set; } = new double[2];
    [JsonPropertyName("ring")] public List<double[]> Ring { get; set; } = new();
    [JsonPropertyName("holes")] public List<List<double[]>> Holes { get; set; } = new();
    [JsonPropertyName("heightMm")] public double HeightMm { get; set; }
    [JsonPropertyName("guid")] public string? Guid { get; set; }
}

public sealed class UnhandledDto
{
    [JsonPropertyName("shape")] public string? Shape { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}
