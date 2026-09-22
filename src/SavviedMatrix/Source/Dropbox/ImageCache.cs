namespace SavviedMatrix.Source.Dropbox;

/// <summary>
/// A flat directory of downloaded images keyed by Dropbox content hash. Doubles as the offline
/// fallback: if Dropbox is unreachable the kiosk replays whatever is still cached, which is why
/// eviction is least-recently-used (by last write time, which <see cref="TryGet"/> touches on
/// every hit) rather than oldest-download-first.
/// </summary>
public sealed class ImageCache
{
    private readonly string _directory;
    private readonly int _maxFiles;

    public ImageCache(string directory, int maxFiles)
    {
        _directory = directory;
        _maxFiles = Math.Max(0, maxFiles);

        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not create image cache directory '{_directory}'", ex);
        }
    }

    /// <summary>
    /// Returns the cached path for a content hash and marks it as freshly used, or null on a miss.
    /// </summary>
    public string? TryGet(string contentHash)
    {
        if (string.IsNullOrEmpty(contentHash)) return null;

        try
        {
            if (!Directory.Exists(_directory)) return null;

            foreach (var path in Directory.EnumerateFiles(_directory, contentHash + "*"))
            {
                // The wildcard can over-match (8.3 aliases, longer hashes); check the stem exactly.
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), contentHash, StringComparison.Ordinal))
                    continue;

                try
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    // A failed touch only makes this file look older than it is.
                    Log.Warn($"Could not touch cached file '{path}': {ex.Message}");
                }

                return path;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Image cache lookup for '{contentHash}' failed", ex);
        }

        return null;
    }

    /// <summary>Writes bytes into the cache and returns the file path.</summary>
    public string Store(string contentHash, string extension, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (!string.IsNullOrEmpty(extension) && !extension.StartsWith('.'))
            extension = "." + extension;

        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, contentHash + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Every file currently in the cache. Used to build the offline playlist.</summary>
    public IReadOnlyList<string> AllFiles()
    {
        try
        {
            if (!Directory.Exists(_directory)) return Array.Empty<string>();

            var files = Directory.GetFiles(_directory);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not enumerate image cache '{_directory}'", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Deletes least-recently-used files until the cache is back within its limit.
    /// Never throws: a cache that cannot be trimmed must not take the slideshow down with it.
    /// </summary>
    public void Evict()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;

            var files = new List<(string Path, DateTime LastWrite)>();
            foreach (var path in Directory.EnumerateFiles(_directory))
            {
                try
                {
                    files.Add((path, new FileInfo(path).LastWriteTimeUtc));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not stat cached file '{path}': {ex.Message}");
                }
            }

            var doomed = SelectForEviction(files, _maxFiles);
            if (doomed.Count == 0) return;

            int deleted = 0;
            foreach (var path in doomed)
            {
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not evict cached file '{path}': {ex.Message}");
                }
            }

            Log.Info($"Image cache: evicted {deleted} of {files.Count} file(s), limit {_maxFiles}.");
        }
        catch (Exception ex)
        {
            Log.Error($"Image cache eviction failed for '{_directory}'", ex);
        }
    }

    /// <summary>
    /// Chooses which files to delete: oldest first, until only <paramref name="maxFiles"/> remain.
    /// Pure so the policy can be tested without touching a disk; ties broken on path so the
    /// result is deterministic no matter what order the filesystem enumerated in.
    /// </summary>
    public static IReadOnlyList<string> SelectForEviction(
        IReadOnlyList<(string Path, DateTime LastWrite)> files, int maxFiles)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (maxFiles < 0) maxFiles = 0;
        if (files.Count <= maxFiles) return Array.Empty<string>();

        return files
            .OrderBy(f => f.LastWrite)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(files.Count - maxFiles)
            .Select(f => f.Path)
            .ToList();
    }
}
