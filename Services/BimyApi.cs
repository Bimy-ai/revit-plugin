using System.Net;
using System.Net.Http;
using System.Text.Json;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

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

    public static Task<UserObjectsPayload> FetchUserObjectsAsync(string url, string token, CancellationToken ct = default)
        => JsonFetcher.FetchAsync<UserObjectsPayload>(url, token, ct);

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
