namespace SavviedMatrix.Source;

/// <summary>
/// The extensions both sources treat as images. Shared so that a local folder and a
/// Dropbox folder holding the same files produce the same playlist.
/// </summary>
internal static class ImageFileExtensions
{
    private static readonly HashSet<string> Set = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    };

    public static bool IsImage(string nameOrPath) => Set.Contains(Path.GetExtension(nameOrPath));
}

/// <summary>
/// Images read straight off a local directory (the <c>--folder</c> mode). Used for demos and
/// for running without Dropbox credentials. Because nothing is downloaded, the content hash is
/// simply the full path: <see cref="Dropbox.ImageCache"/> is never consulted for this source.
/// </summary>
public sealed class LocalFolderSource : IImageSource
{
    private readonly string _folder;

    public LocalFolderSource(string folder)
    {
        _folder = folder;
    }

    public string Description => _folder;

    public Task<IReadOnlyList<ImageRef>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!Directory.Exists(_folder))
        {
            Log.Warn($"Local folder '{_folder}' does not exist; no images to show.");
            return Task.FromResult<IReadOnlyList<ImageRef>>(Array.Empty<ImageRef>());
        }

        try
        {
            var refs = new List<ImageRef>();
            foreach (var path in Directory.EnumerateFiles(_folder, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (!ImageFileExtensions.IsImage(path)) continue;
                refs.Add(new ImageRef(path, path, Path.GetFileName(path)));
            }

            // Stable order so the slideshow does not reshuffle itself on every cycle.
            refs.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            Log.Info($"Local folder '{_folder}': {refs.Count} image(s).");
            return Task.FromResult<IReadOnlyList<ImageRef>>(refs);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not list local folder '{_folder}'", ex);
            return Task.FromResult<IReadOnlyList<ImageRef>>(Array.Empty<ImageRef>());
        }
    }

    public Task<string> FetchAsync(ImageRef image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(image.Id);
    }
}
