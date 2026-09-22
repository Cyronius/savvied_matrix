using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SavviedMatrix.Source.Dropbox;

/// <summary>
/// Thrown when Dropbox rejects the stored refresh token (the user revoked the app, or the
/// token was invalidated). Distinct from a transient network failure because the caller must
/// stop retrying and tell someone to re-run <c>--auth</c>.
/// </summary>
public class DropboxAuthExpiredException : Exception
{
    public DropboxAuthExpiredException() : base("The Dropbox authorization is no longer valid.") { }
    public DropboxAuthExpiredException(string message) : base(message) { }
    public DropboxAuthExpiredException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The OAuth 2 PKCE dance against Dropbox. PKCE (rather than a client secret) because the exe
/// ships to a kiosk and cannot keep a secret; no <c>redirect_uri</c> because the kiosk has no
/// loopback listener - Dropbox instead shows a code that the operator pastes into
/// <see cref="AuthDialog"/>.
/// </summary>
public static class DropboxAuth
{
    private const string AuthorizeEndpoint = "https://www.dropbox.com/oauth2/authorize";
    private const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";

    /// <summary>The RFC 7636 unreserved character set.</summary>
    private const string VerifierAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    private const int VerifierLength = 64; // RFC 7636 allows 43..128.

    /// <summary>Creates a fresh PKCE code verifier. Must be kept until the code is exchanged.</summary>
    public static string GenerateCodeVerifier()
    {
        var chars = new char[VerifierLength];
        for (int i = 0; i < chars.Length; i++)
        {
            // GetInt32 is rejection-sampled, so the alphabet stays uniformly distributed.
            chars[i] = VerifierAlphabet[RandomNumberGenerator.GetInt32(VerifierAlphabet.Length)];
        }
        return new string(chars);
    }

    /// <summary>The S256 challenge for a verifier: base64url(SHA-256(verifier)), unpadded.</summary>
    public static string CodeChallenge(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    /// <summary>
    /// The URL the operator opens in a browser. Deliberately has no <c>redirect_uri</c>, which
    /// makes Dropbox display a copy-and-paste authorization code instead of redirecting.
    /// </summary>
    public static string BuildAuthorizeUrl(string appKey, string codeChallenge)
    {
        return AuthorizeEndpoint
            + "?client_id=" + Uri.EscapeDataString(appKey)
            + "&response_type=code"
            + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
            + "&code_challenge_method=S256"
            + "&token_access_type=offline";
    }

    /// <summary>Trades the pasted authorization code for a long-lived refresh token.</summary>
    public static async Task<DropboxToken> ExchangeCodeAsync(
        HttpClient http, string appKey, string code, string verifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
            ["client_id"] = appKey
        });

        using var response = await http.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error($"Dropbox code exchange failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            throw new InvalidOperationException(
                $"Dropbox code exchange failed ({(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var refreshToken = GetString(root, "refresh_token");
        if (string.IsNullOrEmpty(refreshToken))
            throw new InvalidOperationException($"Dropbox returned no refresh_token: {body}");

        var accountId = GetString(root, "account_id") ?? "";

        Log.Info($"Dropbox authorization complete for account {accountId}.");
        return new DropboxToken(refreshToken, accountId, DateTimeOffset.Now);
    }

    /// <summary>
    /// Mints a short-lived access token from the refresh token.
    /// Throws <see cref="DropboxAuthExpiredException"/> when the refresh token itself is dead.
    /// </summary>
    public static async Task<(string AccessToken, DateTimeOffset ExpiresAt)> RefreshAsync(
        HttpClient http, string appKey, string refreshToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = appKey
        });

        using var response = await http.PostAsync(TokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (IsInvalidGrant(body))
            {
                Log.Error($"Dropbox refresh token rejected (invalid_grant): {body}");
                throw new DropboxAuthExpiredException(
                    "Dropbox rejected the stored refresh token. Re-run SavviedMatrix with --auth.");
            }

            Log.Error($"Dropbox token refresh failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            throw new InvalidOperationException(
                $"Dropbox token refresh failed ({(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var accessToken = GetString(root, "access_token");
        if (string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException($"Dropbox returned no access_token: {body}");

        int expiresIn = 4 * 60 * 60; // Dropbox's own default if the field is missing.
        if (root.TryGetProperty("expires_in", out var e))
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int seconds)) expiresIn = seconds;
            else if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out int parsed)) expiresIn = parsed;
        }

        return (accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static bool IsInvalidGrant(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return string.Equals(error.GetString(), "invalid_grant", StringComparison.Ordinal);

                // Some Dropbox endpoints nest the tag: {"error": {".tag": "invalid_grant"}}
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty(".tag", out var tag)
                    && tag.ValueKind == JsonValueKind.String)
                {
                    return string.Equals(tag.GetString(), "invalid_grant", StringComparison.Ordinal);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the substring check below.
        }

        return body.Contains("invalid_grant", StringComparison.Ordinal);
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
