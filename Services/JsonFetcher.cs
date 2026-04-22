using System.Net.Http;
using System.Net.Http.Headers;
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

    public static Task<T> FetchAsync<T>(string url, CancellationToken ct = default) where T : class
        => FetchAsync<T>(url, bearerToken: null, ct);

    public static async Task<T> FetchAsync<T>(string url, string? bearerToken, CancellationToken ct = default)
        where T : class
    {
        using var request = NewRequest(url, bearerToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var value = await JsonSerializer.DeserializeAsync<T>(stream, _options, ct).ConfigureAwait(false);
        if (value is null)
            throw new InvalidOperationException("Response body was empty or not valid JSON.");
        return value;
    }

    public static async Task<JsonDocument> FetchDocumentAsync(string url, string? bearerToken, CancellationToken ct = default)
    {
        using var request = NewRequest(url, bearerToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage NewRequest(string url, string? bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return request;
    }
}
