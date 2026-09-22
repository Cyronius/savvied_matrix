using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SavviedMatrix.Source.Dropbox;

/// <summary>One file in a Dropbox folder listing.</summary>
public sealed record DropboxEntry(string PathLower, string Name, string ContentHash, long Size);

/// <summary>
/// The thin slice of the Dropbox HTTP API the kiosk needs: list a folder, pull a thumbnail,
/// download an original. Owns the access token lifecycle and the retry policy, so callers can
/// treat a failure as "Dropbox is genuinely unavailable" rather than "try again in a second".
/// </summary>
public sealed class DropboxClient
{
    private const string ListFolderUrl = "https://api.dropboxapi.com/2/files/list_folder";
    private const string ListFolderContinueUrl = "https://api.dropboxapi.com/2/files/list_folder/continue";
    private const string ThumbnailUrl = "https://content.dropboxapi.com/2/files/get_thumbnail_v2";
    private const string DownloadUrl = "https://content.dropboxapi.com/2/files/download";

    private const int MaxAttempts = 5;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>Refresh this far ahead of expiry so a long download cannot straddle the boundary.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _appKey;
    private readonly DropboxToken _token;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public DropboxClient(HttpClient http, string appKey, DropboxToken token)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _appKey = appKey;
        _token = token ?? throw new ArgumentNullException(nameof(token));
    }

    /// <summary>The account the stored refresh token belongs to.</summary>
    public string AccountId => _token.AccountId;

    /// <summary>
    /// Returns a valid access token, minting a new one when fewer than five minutes remain.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - RefreshMargin)
                return _accessToken;

            var (accessToken, expiresAt) =
                await DropboxAuth.RefreshAsync(_http, _appKey, _token.RefreshToken, ct).ConfigureAwait(false);

            _accessToken = accessToken;
            _accessTokenExpiresAt = expiresAt;
            Log.Info($"Dropbox access token refreshed; valid until {expiresAt.ToLocalTime():HH:mm:ss}.");
            return accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>Lists the files in a folder, following pagination. Subfolders are ignored.</summary>
    public async Task<IReadOnlyList<DropboxEntry>> ListFolderAsync(string path, CancellationToken ct)
    {
        var entries = new List<DropboxEntry>();
        var body = $"{{\"path\":{JsonString(NormalizeFolder(path))},\"recursive\":false}}";
        var url = ListFolderUrl;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string json;
            using (var response = await SendAsync(
                       url, access => JsonRequest(url, access, body), ct).ConfigureAwait(false))
            {
                json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (GetString(item, ".tag") != "file") continue;

                    var pathLower = GetString(item, "path_lower") ?? GetString(item, "path_display");
                    if (string.IsNullOrEmpty(pathLower)) continue;

                    long size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                        ? s.GetInt64()
                        : 0L;

                    entries.Add(new DropboxEntry(
                        pathLower,
                        GetString(item, "name") ?? Path.GetFileName(pathLower),
                        GetString(item, "content_hash") ?? "",
                        size));
                }
            }

            bool hasMore = root.TryGetProperty("has_more", out var more)
                           && more.ValueKind == JsonValueKind.True;
            var cursor = GetString(root, "cursor");

            if (!hasMore || string.IsNullOrEmpty(cursor)) break;

            url = ListFolderContinueUrl;
            body = $"{{\"cursor\":{JsonString(cursor)}}}";
        }

        var where = NormalizeFolder(path);
        Log.Info($"Dropbox listed {entries.Count} file(s) in {(where.Length == 0 ? "the app folder root" : $"'{where}'")}.");
        return entries;
    }

    /// <summary>
    /// Fetches a server-rendered JPEG preview. Much cheaper than the original for a 4K photo,
    /// and the matrix renderer never needs more than about a thousand pixels across.
    /// </summary>
    public async Task<byte[]> GetThumbnailAsync(string pathLower, CancellationToken ct)
    {
        var arg = "{\"resource\":{\".tag\":\"path\",\"path\":" + JsonString(pathLower) + "},"
                + "\"format\":\"jpeg\",\"size\":\"w1024h768\",\"mode\":\"bestfit\"}";

        using var response = await SendAsync(
            ThumbnailUrl, access => ContentRequest(ThumbnailUrl, access, arg), ct).ConfigureAwait(false);

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Downloads the original file bytes.</summary>
    public async Task<byte[]> DownloadAsync(string pathLower, CancellationToken ct)
    {
        var arg = "{\"path\":" + JsonString(pathLower) + "}";

        using var response = await SendAsync(
            DownloadUrl, access => ContentRequest(DownloadUrl, access, arg), ct).ConfigureAwait(false);

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    // ---- request construction -------------------------------------------------------------

    private static HttpRequestMessage JsonRequest(string url, string accessToken, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage ContentRequest(string url, string accessToken, string apiArg)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // TryAddWithoutValidation: the value is JSON, which the header parser would otherwise
        // try to interpret. AsciiEscape first - see the helper for why.
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", AsciiEscape(apiArg));

        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = content;
        return request;
    }

    // ---- transport ------------------------------------------------------------------------

    /// <summary>
    /// Sends a request, refreshing the token once on 401 and backing off on 429/5xx.
    /// The request is built by a factory because an <see cref="HttpRequestMessage"/> cannot be
    /// resent after a failed attempt.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        string url, Func<string, HttpRequestMessage> build, CancellationToken ct)
    {
        int attempt = 0;
        bool refreshedOnUnauthorized = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            var accessToken = await GetAccessTokenAsync(ct).ConfigureAwait(false);

            HttpResponseMessage response;
            using (var request = build(accessToken))
            {
                response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }

            if (response.IsSuccessStatusCode) return response;

            int status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedOnUnauthorized)
            {
                response.Dispose();
                refreshedOnUnauthorized = true;
                attempt--; // A token refresh is not a retry of a failing server.
                Log.Warn($"Dropbox returned 401 for {url}; refreshing the access token and retrying.");
                await InvalidateAccessTokenAsync(ct).ConfigureAwait(false);
                continue;
            }

            bool retryable = status == 429 || status >= 500;
            if (retryable && attempt < MaxAttempts)
            {
                var delay = RetryDelay(response, attempt);
                response.Dispose();
                Log.Warn($"Dropbox {url} returned {status}; attempt {attempt}/{MaxAttempts}, " +
                         $"retrying in {delay.TotalSeconds:0.#}s.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            var body = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);
            response.Dispose();

            var reason = retryable
                ? $"gave up after {attempt} attempt(s)"
                : "failed";
            Log.Error($"Dropbox {url} {reason}: {status}: {Truncate(body, 500)}");
            throw new HttpRequestException($"Dropbox {url} {reason} with HTTP {status}: {Truncate(body, 500)}");
        }
    }

    private async Task InvalidateAccessTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _accessToken = null;
            _accessTokenExpiresAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>Honours Retry-After when Dropbox sends one; otherwise 1s, 2s, 4s ... up to 60s.</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is not null)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero) return delta;
            if (retryAfter.Date is { } date)
            {
                var wait = date - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) return wait;
            }
        }

        double seconds = Math.Pow(2, Math.Min(attempt - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    private static async Task<string> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"(response body unreadable: {ex.Message})";
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>
    /// Escapes every non-ASCII character as \uXXXX. HTTP header values are Latin-1 at best, so a
    /// file called "cafe.jpg" with an accent or a CJK name would otherwise be mangled or rejected
    /// outright on the way to Dropbox-API-Arg. Dropbox accepts the JSON escape form.
    /// </summary>
    internal static string AsciiEscape(string value)
    {
        bool needsEscaping = false;
        foreach (char c in value)
        {
            if (c > 0x7F) { needsEscaping = true; break; }
        }
        if (!needsEscaping) return value;

        var sb = new StringBuilder(value.Length + 16);
        foreach (char c in value)
        {
            if (c > 0x7F) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Serializes a string as a JSON literal, quotes included.</summary>
    private static string JsonString(string value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// "" means the root of the app folder; anything else needs a leading slash and no trailing one.
    /// </summary>
    private static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";

        var trimmed = folder.Trim().Replace('\\', '/').TrimEnd('/');
        if (trimmed.Length == 0) return "";
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
