using System.Net;

namespace RevitWallsPlugin.Services;

/// <summary>
/// A non-success HTTP answer from the BIMy API, carrying the status and the
/// server's own <c>{ error, message }</c> so commands can show it verbatim
/// instead of a generic failure. <see cref="StatusCode"/> is null when the
/// request never got a response (network error).
/// </summary>
internal sealed class BimyFetchException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string? ServerMessage { get; }
    public string RawBody { get; }

    public BimyFetchException(HttpStatusCode statusCode, string? serverMessage, string rawBody)
        : base(serverMessage ?? $"Request failed ({(int)statusCode} {statusCode}).")
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
        RawBody = rawBody ?? string.Empty;
    }
}
