using System.Net;
using System.Net.Http;
using System.Text.Json;
using BimyRevit.Models;

namespace BimyRevit.Services;

internal static class BimyApi
{
    private static readonly JsonSerializerOptions _userJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    public static async Task<BimyUser?> VerifyAsync(BimyEnvironment env, string token, CancellationToken ct = default)
    {
        var url = BimyEnvironments.AuthUrl(env);
        Log.Info($"Verifying token for env={BimyEnvironments.DisplayName(env)} ({url}).");
        try
        {
            // Auth response shape varies: either { user: {...} } or a flat user object.
            using var doc = await JsonFetcher.FetchDocumentAsync(url, token, ct).ConfigureAwait(false);
            var user = ExtractUser(doc.RootElement);
            if (user is null || string.IsNullOrWhiteSpace(user.Id))
            {
                Log.Warn("Auth response did not include a recognisable user object.");
                return null;
            }
            Log.Info($"Auth OK as {user.DisplayLabel}.");
            return user;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized
                                           || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            Log.Warn($"Auth rejected ({(int?)ex.StatusCode} {ex.StatusCode}).");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Auth verification failed for {url}", ex);
            throw;
        }
    }

    /// <summary>
    /// The workspace's projects, newest first. Never throws: the picker has a
    /// paste-an-id fallback, so a list that can't be fetched degrades to that
    /// rather than blocking the pull.
    /// </summary>
    public static async Task<IReadOnlyList<BimyProject>> ListProjectsAsync(
        BimyEnvironment env, string token, CancellationToken ct = default)
    {
        var url = BimyEnvironments.ProjectsUrl(env);
        try
        {
            var projects = await JsonFetcher.FetchAsync<List<BimyProject>>(url, token, ct).ConfigureAwait(false);
            var usable = projects.Where(p => !string.IsNullOrWhiteSpace(p.Id)).ToList();
            Log.Info($"Listed {usable.Count} project(s) from {BimyEnvironments.DisplayName(env)}.");
            return usable;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list projects from {url}: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<BimyProject>();
        }
    }

    private static BimyUser? ExtractUser(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("user", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return Deserialize(nested);

        if (root.TryGetProperty("_id", out _)
            || root.TryGetProperty("userId", out _)
            || root.TryGetProperty("id", out _)
            || root.TryGetProperty("email", out _))
            return Deserialize(root);

        return null;
    }

    private static BimyUser? Deserialize(JsonElement element)
        => JsonSerializer.Deserialize<BimyUser>(element.GetRawText(), _userJsonOptions);
}
