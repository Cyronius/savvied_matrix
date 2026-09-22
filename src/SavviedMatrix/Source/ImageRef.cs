namespace SavviedMatrix.Source;

/// <summary>
/// An image the playlist can show. <see cref="Id"/> is the source-specific identifier
/// (a Dropbox path_lower, or a local file path); <see cref="ContentHash"/> is the cache key.
/// </summary>
public readonly record struct ImageRef(string Id, string ContentHash, string Name);

/// <summary>
/// Where images come from. Both implementations hand back a path to a file on local disk;
/// the Dropbox one downloads and caches, the local one just returns the path it was given.
/// </summary>
public interface IImageSource
{
    /// <summary>A human-readable description of the source, used in the "no images" message.</summary>
    string Description { get; }

    /// <summary>Lists the available images. Called once per playlist cycle.</summary>
    Task<IReadOnlyList<ImageRef>> ListAsync(CancellationToken ct);

    /// <summary>Returns a local file path for the image, fetching it if necessary.</summary>
    Task<string> FetchAsync(ImageRef image, CancellationToken ct);
}
