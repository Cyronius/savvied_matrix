using System.Text.Json;

namespace SavviedMatrix.Source.Dropbox;

/// <summary>
/// The long-lived half of the Dropbox credentials. Only the refresh token is persisted;
/// access tokens are short-lived and are re-minted at runtime by <see cref="DropboxClient"/>.
/// </summary>
public record DropboxToken(string RefreshToken, string AccountId, DateTimeOffset ObtainedAt);

/// <summary>
/// Reads and writes <c>token.json</c> beside the exe. The kiosk is authorized once by hand
/// (<c>--auth</c>) and must survive reboots without anyone signing in again, so the token
/// lives on disk rather than in memory or in the registry.
/// </summary>
public static class DropboxTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Full path of the token file.</summary>
    public static string TokenPath => Path.Combine(AppContext.BaseDirectory, "token.json");

    public static bool Exists() => File.Exists(TokenPath);

    /// <summary>Returns the stored token, or null when there is none or it cannot be read.</summary>
    public static DropboxToken? Load()
    {
        var path = TokenPath;
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var token = JsonSerializer.Deserialize<DropboxToken>(json, JsonOptions);
            if (token is null || string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                Log.Warn($"token.json at '{path}' has no refresh token; re-run with --auth.");
                return null;
            }
            return token;
        }
        catch (Exception ex)
        {
            Log.Warn($"token.json at '{path}' could not be read ({ex.Message}); re-run with --auth.");
            return null;
        }
    }

    /// <summary>Writes the token, replacing any existing one.</summary>
    public static void Save(DropboxToken token)
    {
        var path = TokenPath;
        var json = JsonSerializer.Serialize(token, JsonOptions);
        File.WriteAllText(path, json);
        Log.Info($"Dropbox token saved to '{path}' (account {token.AccountId}).");
    }
}
