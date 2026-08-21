using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal sealed class Session
{
    public BimyEnvironment Environment { get; init; }
    public string Token { get; init; } = string.Empty;
}

internal static class SessionStore
{
    private static readonly string _path = ResolvePath();
    private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("RevitWallsPlugin.session.v1");

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static Session? TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var raw = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var record = JsonSerializer.Deserialize<PersistedSession>(raw, _json);
            if (record is null || string.IsNullOrWhiteSpace(record.EncryptedTokenBase64))
                return null;

            var protectedBytes = Convert.FromBase64String(record.EncryptedTokenBase64);
            var plain = ProtectedData.Unprotect(protectedBytes, _entropy, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(plain);
            if (string.IsNullOrWhiteSpace(token)) return null;

            return new Session
            {
                Environment = record.Environment,
                Token = token,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not load session file: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static void Save(BimyEnvironment env, string token)
    {
        try
        {
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token), _entropy, DataProtectionScope.CurrentUser);
            var record = new PersistedSession
            {
                Environment = env,
                EncryptedTokenBase64 = Convert.ToBase64String(protectedBytes),
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(record, _json));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save session file: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete session file: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // %LOCALAPPDATA%\BIMy\session.json. Older builds kept this next to the DLL,
    // which meant a plugin UPDATE (the installer replaces that folder) signed the
    // user out. If the new file doesn't exist yet but an old one does, move it
    // across once — the DPAPI blob is bound to the Windows user, not the path,
    // so it still decrypts.
    private static string ResolvePath()
    {
        var path = BimyPaths.SessionFile;
        try
        {
            if (!File.Exists(path))
            {
                var legacyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var legacy = legacyDir is null ? null : Path.Combine(legacyDir, "RevitWallsPlugin.session.json");
                if (legacy is not null && File.Exists(legacy))
                {
                    File.Copy(legacy, path, overwrite: false);
                    Log.Info($"Migrated saved session from {legacy}.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Session migration skipped: {ex.GetType().Name}: {ex.Message}");
        }
        return path;
    }

    private sealed class PersistedSession
    {
        [JsonPropertyName("environment")]
        public BimyEnvironment Environment { get; set; }

        [JsonPropertyName("encryptedToken")]
        public string EncryptedTokenBase64 { get; set; } = string.Empty;
    }
}
