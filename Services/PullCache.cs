using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

/// <summary>
/// What this machine already pulled, per environment + project: the published
/// blob's ETag, when it was published, and where the converted .rvt was saved.
///
/// Two things depend on it. The pull sends the stored ETag as
/// <c>If-None-Match</c>, so re-pulling a project nobody has republished answers
/// 304 in a few hundred bytes instead of re-downloading megabytes of STEP and
/// re-running Revit's IFC importer for a minute — and the plugin can offer to
/// just re-open the file it already has. And the picker can pre-select the
/// project the user pulled last, per environment, so the common case ("pull the
/// same model again after editing it in BIMy") is two clicks.
///
/// Every operation is best-effort: a corrupt or unwritable cache degrades to a
/// full download, never to a failed command.
/// </summary>
internal static class PullCache
{
    private static readonly object _gate = new();
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal sealed class PullRecord
    {
        [JsonPropertyName("projectId")] public string ProjectId { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string? Name { get; set; }
        /// <summary>The blob ETag last downloaded, replayed as If-None-Match.</summary>
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        /// <summary>Server's x-ifc-updated for that blob (ISO 8601).</summary>
        [JsonPropertyName("publishedAt")] public string? PublishedAt { get; set; }
        /// <summary>Absolute path of the .rvt this machine saved the pull to.</summary>
        [JsonPropertyName("rvtPath")] public string? RvtPath { get; set; }
        [JsonPropertyName("pulledAt")] public DateTimeOffset? PulledAt { get; set; }

        /// <summary>True when the saved .rvt is still on disk and openable.</summary>
        [JsonIgnore]
        public bool HasLocalCopy => !string.IsNullOrWhiteSpace(RvtPath) && File.Exists(RvtPath);
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("lastProjectId")] public Dictionary<string, string> LastProjectId { get; set; } = new();
        [JsonPropertyName("pulls")] public Dictionary<string, PullRecord> Pulls { get; set; } = new();
    }

    public static PullRecord? Get(BimyEnvironment env, string projectId)
    {
        lock (_gate)
        {
            var file = Read();
            return file.Pulls.TryGetValue(Key(env, projectId), out var rec) ? rec : null;
        }
    }

    /// <summary>
    /// Every record for one environment, keyed by project id. The picker needs
    /// "when did I last pull this?" for every row it draws; asking
    /// <see cref="Get"/> per row would re-read and re-parse the whole file once
    /// per project.
    /// </summary>
    public static IReadOnlyDictionary<string, PullRecord> GetAll(BimyEnvironment env)
    {
        var prefix = EnvKey(env) + ":";
        var result = new Dictionary<string, PullRecord>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var (key, record) in Read().Pulls)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var projectId = string.IsNullOrWhiteSpace(record.ProjectId) ? key[prefix.Length..] : record.ProjectId;
                result[projectId] = record;
            }
        }
        return result;
    }

    public static void Save(BimyEnvironment env, PullRecord record)
    {
        lock (_gate)
        {
            var file = Read();
            record.PulledAt = DateTimeOffset.Now;
            file.Pulls[Key(env, record.ProjectId)] = record;
            file.LastProjectId[EnvKey(env)] = record.ProjectId;
            Write(file);
        }
    }

    public static string? LastProjectId(BimyEnvironment env)
    {
        lock (_gate)
        {
            var file = Read();
            return file.LastProjectId.TryGetValue(EnvKey(env), out var id) && !string.IsNullOrWhiteSpace(id)
                ? id
                : null;
        }
    }

    public static void RememberProjectId(BimyEnvironment env, string projectId)
    {
        lock (_gate)
        {
            var file = Read();
            file.LastProjectId[EnvKey(env)] = projectId;
            Write(file);
        }
    }

    private static string EnvKey(BimyEnvironment env) => env.ToString().ToLowerInvariant();
    private static string Key(BimyEnvironment env, string projectId) => $"{EnvKey(env)}:{projectId}";

    private static CacheFile Read()
    {
        try
        {
            var path = BimyPaths.PullCacheFile;
            if (!File.Exists(path)) return new CacheFile();
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw)) return new CacheFile();
            return JsonSerializer.Deserialize<CacheFile>(raw, _json) ?? new CacheFile();
        }
        catch (Exception ex)
        {
            Log.Warn($"Pull cache unreadable, starting fresh: {ex.GetType().Name}: {ex.Message}");
            return new CacheFile();
        }
    }

    private static void Write(CacheFile file)
    {
        try { File.WriteAllText(BimyPaths.PullCacheFile, JsonSerializer.Serialize(file, _json)); }
        catch (Exception ex) { Log.Warn($"Could not write pull cache: {ex.GetType().Name}: {ex.Message}"); }
    }
}
