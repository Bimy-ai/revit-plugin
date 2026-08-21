using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BimyRevit.Services;

internal static class JsonFetcher
{
    // Ten minutes, not thirty seconds: this client also pulls published models,
    // and a large building is tens of megabytes of STEP text over whatever
    // connection the user's office has. HttpClient's timeout covers the WHOLE
    // response, body included, so a 30 s cap silently killed big pulls midway.
    // The small JSON calls are unaffected — they finish long before it matters.
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
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

    /// <summary>What a conditional download came back with.</summary>
    internal sealed class DownloadResult
    {
        /// <summary>True when the server answered 304 — nothing was written to disk.</summary>
        public bool NotModified { get; init; }
        /// <summary>Strong ETag of the blob, to replay as If-None-Match next time.</summary>
        public string? ETag { get; init; }
        /// <summary>Server-suggested file name (<c>x-ifc-name</c>), already URL-decoded.</summary>
        public string? SuggestedName { get; init; }
        /// <summary>When the blob was published (<c>x-ifc-updated</c>, ISO 8601).</summary>
        public string? PublishedAt { get; init; }
        /// <summary>Bytes written. Zero on 304.</summary>
        public long Bytes { get; init; }
    }

    /// <summary>
    /// Download a URL straight to <paramref name="destPath"/>, optionally
    /// conditionally: pass the ETag from the previous pull as
    /// <paramref name="ifNoneMatch"/> and a server that has nothing new answers
    /// 304, which comes back as <see cref="DownloadResult.NotModified"/> with the
    /// destination file untouched. That turns "pull the same model again" from a
    /// multi-megabyte download plus a full IFC conversion into one round trip.
    ///
    /// On any other non-success status the server's JSON <c>{ error, message }</c>
    /// is surfaced through <see cref="BimyFetchException"/> so the caller can show
    /// a real explanation (e.g. the 404 "not published to Revit yet").
    /// </summary>
    public static async Task<DownloadResult> DownloadToFileAsync(
        string url,
        string? bearerToken,
        string destPath,
        string? ifNoneMatch = null,
        IProgress<long>? bytesRead = null,
        CancellationToken ct = default)
    {
        using var request = NewRequest(url, bearerToken);
        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            // TryParseAdd, not Add: a malformed cached ETag must not throw and
            // break the pull — it just means the request goes out unconditional.
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var etag = response.Headers.ETag?.ToString();
        var name = Header(response, "x-ifc-name");
        var published = Header(response, "x-ifc-updated");

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new DownloadResult
            {
                NotModified = true,
                ETag = etag ?? ifNoneMatch,
                SuggestedName = Decode(name),
                PublishedAt = published,
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
            throw new BimyFetchException(response.StatusCode, ExtractServerMessage(body), body);
        }

        // Write to a sibling .part and move into place, so an aborted download
        // can never leave a truncated file that the next run happily opens.
        var partPath = destPath + ".part";
        long total = 0;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                bytesRead?.Report(total);
            }
        }
        File.Move(partPath, destPath, overwrite: true);

        return new DownloadResult
        {
            ETag = etag,
            SuggestedName = Decode(name),
            PublishedAt = published,
            Bytes = total,
        };
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Uri.UnescapeDataString(value); }
        catch { return value; }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString();
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    return e.GetString();
            }
        }
        catch { /* not JSON — fall through */ }
        return null;
    }

    private static HttpRequestMessage NewRequest(string url, string? bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return request;
    }
}
