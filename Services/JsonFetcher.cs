using System.Net.Http;
using System.Text.Json;

namespace RevitWallsPlugin.Services;

internal static class JsonFetcher
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static async Task<T> FetchAsync<T>(string url, CancellationToken ct = default)
        where T : class
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var value = await JsonSerializer.DeserializeAsync<T>(stream, _options, ct).ConfigureAwait(false);
        if (value is null)
            throw new InvalidOperationException("Response body was empty or not valid JSON.");

        return value;
    }
}
