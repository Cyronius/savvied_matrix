namespace SavviedMatrix;

/// <summary>
/// A shuffle bag. Every item is served exactly once per cycle in random order, then the
/// caller refills from a fresh listing. Plain random picking would show the same picture
/// twice in a minute and skip others for an hour, which on a wall display is very obvious.
/// </summary>
public sealed class Playlist<T>
{
    private readonly Random _rng;
    private readonly List<T> _bag = new();
    private bool _hasLast;
    private T? _last;

    public Playlist(Random rng) => _rng = rng;

    public bool IsEmpty => _bag.Count == 0;
    public int Remaining => _bag.Count;

    /// <summary>
    /// Starts a new cycle from <paramref name="items"/>, discarding anything left over.
    /// The first item of the new cycle is never the last item of the previous one,
    /// so a repeat is never adjacent across the seam.
    /// </summary>
    public void Refill(IReadOnlyList<T> items)
    {
        _bag.Clear();
        _bag.AddRange(items);

        Shuffle();

        if (_hasLast && _bag.Count > 1
            && EqualityComparer<T>.Default.Equals(_bag[^1], _last!))
        {
            // The bag is drained from the end, so the last element is served first.
            int swap = _rng.Next(_bag.Count - 1);
            (_bag[swap], _bag[^1]) = (_bag[^1], _bag[swap]);
        }
    }

    public bool TryNext(out T item)
    {
        if (_bag.Count == 0)
        {
            item = default!;
            return false;
        }

        int index = _bag.Count - 1;
        item = _bag[index];
        _bag.RemoveAt(index);

        _last = item;
        _hasLast = true;
        return true;
    }

    private void Shuffle()
    {
        for (int i = _bag.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
    }
}
