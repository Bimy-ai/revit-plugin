using System.Text.Json.Serialization;

namespace BimyRevit.Models;

/// <summary>
/// One row of the project picker. Read from <c>GET /api/data?model=Project</c>
/// — the generic CRUD list every BIMy deployment already serves — so the picker
/// works against production without waiting on an API release. Only the fields
/// the list actually shows are declared; the rest of the document is ignored.
/// </summary>
public sealed class BimyProject
{
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Project emoji chosen in BIMy's project settings, if any.</summary>
    [JsonPropertyName("emoji")]
    public string? Emoji { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(untitled project)" : Name!;

    /// <summary>Newest signal we have for "when did this project last change".</summary>
    [JsonIgnore]
    public DateTimeOffset? Touched => UpdatedAt ?? CreatedAt;
}
