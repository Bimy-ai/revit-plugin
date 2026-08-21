using System.Text.Json.Serialization;

namespace BimyRevit.Models;

public sealed class BimyUser
{
    /// <summary>Mongo ObjectId from the MetaUser document (returned as `_id`).</summary>
    [JsonPropertyName("_id")]
    public string? MongoId { get; set; }

    /// <summary>Plain `id` (some responses use this instead of `_id`).</summary>
    [JsonPropertyName("id")]
    public string? PlainId { get; set; }

    /// <summary>API-token-flow specifically attaches the userId field.</summary>
    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonIgnore]
    public string? Id => UserId ?? MongoId ?? PlainId;

    [JsonIgnore]
    public string DisplayLabel => !string.IsNullOrWhiteSpace(Email)
        ? Email!
        : (Name ?? Id ?? "unknown");
}
