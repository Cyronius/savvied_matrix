namespace SavviedMatrix.Source.Dropbox;

/// <summary>
/// Images from a Dropbox folder, cached on disk. Every failure path degrades to the cache rather
/// than throwing: the kiosk runs unattended on a flaky network and a blank screen is worse than
/// yesterday's photos.
/// </summary>
public sealed class DropboxSource : IImageSource
{
    private readonly DropboxClient _client;
    private readonly ImageCache _cache;
    private readonly string _folder;

    public DropboxSource(DropboxClient client, ImageCache cache, string folder)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _folder = folder ?? "";
    }

    /// <summary>
    /// Set when Dropbox rejects the stored refresh token. The UI uses it to say "re-run --auth"
    /// instead of blaming the network; the slideshow keeps running from the cache meanwhile.
    /// </summary>
    public bool AuthExpired { get; private set; }

    public string Description =>
        string.IsNullOrWhiteSpace(_folder) ? "Dropbox App folder" : $"Dropbox folder {_folder}";

    public async Task<IReadOnlyList<ImageRef>> ListAsync(CancellationToken ct)
    {
        try
        {
            var entries = await _client.ListFolderAsync(_folder, ct).ConfigureAwait(false);

            var refs = new List<ImageRef>();
            foreach (var entry in entries)
            {
                if (!ImageFileExtensions.IsImage(entry.Name)) continue;

                // No content hash (rare, but Dropbox allows it) means no stable cache key;
                // fall back to the path so the file is at least still shown.
                var hash = string.IsNullOrEmpty(entry.ContentHash) ? entry.PathLower : entry.ContentHash;
                refs.Add(new ImageRef(entry.PathLower, hash, entry.Name));
            }

            _cache.Evict();

            Log.Info($"{Description}: {refs.Count} image(s) of {entries.Count} file(s).");
            return refs;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DropboxAuthExpiredException ex)
        {
            AuthExpired = true;
            Log.Error("Dropbox authorization has expired; re-run SavviedMatrix with --auth", ex);
            return CachedRefs();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not list {Description}; falling back to the local cache", ex);
            return CachedRefs();
        }
    }

    public async Task<string> FetchAsync(ImageRef image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var cached = _cache.TryGet(image.ContentHash);
        if (cached is not null) return cached;

        // Offline-fallback refs already point at a file on disk.
        if (!string.IsNullOrEmpty(image.Id) && File.Exists(image.Id)) return image.Id;

        try
        {
            var bytes = await _client.GetThumbnailAsync(image.Id, ct).ConfigureAwait(false);
            return _cache.Store(image.ContentHash, ".jpg", bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Dropbox refuses thumbnails for formats it cannot render (and for very large files),
            // so the original is the fallback, not a hard failure.
            Log.Warn($"No Dropbox thumbnail for '{image.Name}' ({ex.GetType().Name}: {ex.Message}); " +
                     "downloading the original.");
        }

        var original = await _client.DownloadAsync(image.Id, ct).ConfigureAwait(false);
        var extension = Path.GetExtension(image.Name);
        if (string.IsNullOrEmpty(extension)) extension = Path.GetExtension(image.Id);
        if (string.IsNullOrEmpty(extension)) extension = ".bin";

        return _cache.Store(image.ContentHash, extension, original);
    }

    /// <summary>The offline playlist: whatever survived in the cache, keyed by its own file name.</summary>
    private IReadOnlyList<ImageRef> CachedRefs()
    {
        var refs = new List<ImageRef>();
        foreach (var path in _cache.AllFiles())
        {
            if (!ImageFileExtensions.IsImage(path)) continue;

            var stem = Path.GetFileNameWithoutExtension(path);
            refs.Add(new ImageRef(path, stem, stem));
        }

        Log.Info($"Offline fallback: {refs.Count} cached image(s).");
        return refs;
    }
}
