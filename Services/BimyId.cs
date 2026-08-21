using System.Text.RegularExpressions;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Recovers a BIMy project id from whatever the user pasted. People copy the
/// browser's address bar far more often than they copy a bare id, and BIMy has
/// several URL shapes for the same project (<c>/projects/&lt;id&gt;</c>, the
/// regulations sub-page, <c>/api/data/&lt;id&gt;</c>), so matching one specific
/// route would reject perfectly good input.
/// </summary>
internal static class BimyId
{
    private static readonly Regex _bare = new(@"^[0-9a-fA-F]{24}$", RegexOptions.Compiled);

    // Any 24-hex run that isn't glued to more hex on either side. Broad on
    // purpose: it accepts every current URL shape and every future one.
    private static readonly Regex _embedded = new(
        @"(?<![0-9a-fA-F])([0-9a-fA-F]{24})(?![0-9a-fA-F])", RegexOptions.Compiled);

    /// <summary>The 24-character hex id, lower-cased, or null if there isn't one.</summary>
    public static string? Extract(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        if (_bare.IsMatch(trimmed)) return trimmed.ToLowerInvariant();

        var match = _embedded.Match(trimmed);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }
}
