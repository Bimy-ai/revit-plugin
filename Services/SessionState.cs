using BimyRevit.Models;

namespace BimyRevit.Services;

internal static class SessionState
{
    private static readonly object _gate = new();
    private static Session? _session;
    private static BimyUser? _user;

    public static bool IsConnected
    {
        get { lock (_gate) return _session is not null && _user is not null; }
    }

    public static BimyUser? Current
    {
        get { lock (_gate) return _user; }
    }

    public static BimyEnvironment? Environment
    {
        get { lock (_gate) return _session?.Environment; }
    }

    public static string? Token
    {
        get { lock (_gate) return _session?.Token; }
    }

    public static void SetConnected(Session session, BimyUser user)
    {
        lock (_gate)
        {
            _session = session;
            _user = user;
        }
    }

    public static void Clear()
    {
        lock (_gate)
        {
            _session = null;
            _user = null;
        }
    }

    /// <summary>
    /// Loads the persisted session (if any) and re-verifies it against the API.
    /// On success, populates in-memory state. On failure, clears persisted + memory state.
    /// Returns true when the session is valid.
    /// </summary>
    public static async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var persisted = SessionStore.TryLoad();
        if (persisted is null)
        {
            Clear();
            return false;
        }

        try
        {
            var user = await BimyApi.VerifyAsync(persisted.Environment, persisted.Token, ct).ConfigureAwait(false);
            if (user is null)
            {
                SessionStore.Clear();
                Clear();
                return false;
            }

            SetConnected(persisted, user);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Session refresh failed: {ex.GetType().Name}: {ex.Message}");
            // Network error — keep persisted session, but mark as disconnected for now.
            Clear();
            return false;
        }
    }
}
