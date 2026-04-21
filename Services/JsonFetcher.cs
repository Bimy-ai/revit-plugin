using System.Net.Http;
using System.Text.Json;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal static class JsonFetcher
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static async Task<WallsPayload> FetchAsync(string url, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        var payload = await JsonSerializer.DeserializeAsync<WallsPayload>(stream, options, ct).ConfigureAwait(false);
        if (payload is null)
            throw new InvalidOperationException("Response body was empty or not valid JSON.");
        if (payload.Walls.Count == 0)
            throw new InvalidOperationException("JSON contained no walls.");

        return payload;
    }
}
