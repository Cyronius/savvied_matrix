// Traces: MATRIX-PLAYLIST (canonical spec: specs/matrix/spec.md)
using SavviedMatrix;

namespace SavviedMatrix.Tests;

public class PlaylistTests
{
    private static string[] Items(int n) => Enumerable.Range(0, n).Select(i => $"img{i}").ToArray();

    private static List<string> DrainOneCycle(Playlist<string> playlist)
    {
        var seen = new List<string>();
        while (playlist.TryNext(out var item)) seen.Add(item);
        return seen;
    }

    [Fact]
    public void StartsEmpty()
    {
        var playlist = new Playlist<string>(new Random(1));

        Assert.True(playlist.IsEmpty);
        Assert.False(playlist.TryNext(out _));
    }

    [Fact]
    public void ACycleServesEveryItemExactlyOnce()
    {
        var items = Items(25);
        var playlist = new Playlist<string>(new Random(1));
        playlist.Refill(items);

        var seen = DrainOneCycle(playlist);

        Assert.Equal(25, seen.Count);
        Assert.Equal(items.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public void TheOrderIsShuffledRatherThanTheListingOrder()
    {
        var items = Items(50);
        var playlist = new Playlist<string>(new Random(12345));
        playlist.Refill(items);

        var seen = DrainOneCycle(playlist);

        Assert.NotEqual(items, seen);
        Assert.Equal(items.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public void RemainingCountsDownToEmpty()
    {
        var playlist = new Playlist<string>(new Random(2));
        playlist.Refill(Items(3));

        Assert.Equal(3, playlist.Remaining);
        playlist.TryNext(out _);
        Assert.Equal(2, playlist.Remaining);
        playlist.TryNext(out _);
        playlist.TryNext(out _);
        Assert.True(playlist.IsEmpty);
    }

    [Fact]
    public void RefillStartsANewCycleAndDiscardsLeftovers()
    {
        var playlist = new Playlist<string>(new Random(3));
        playlist.Refill(Items(10));
        playlist.TryNext(out _);

        playlist.Refill(Items(4));

        Assert.Equal(4, playlist.Remaining);
        Assert.Equal(4, DrainOneCycle(playlist).Count);
    }

    [Fact]
    public void NewFilesAppearInTheFollowingCycle()
    {
        var playlist = new Playlist<string>(new Random(4));
        playlist.Refill(Items(3));
        DrainOneCycle(playlist);

        playlist.Refill(new[] { "img0", "img1", "img2", "newcomer" });
        var seen = DrainOneCycle(playlist);

        Assert.Contains("newcomer", seen);
    }

    [Fact]
    public void TheSameImageNeverAppearsTwiceInARowAcrossTheSeam()
    {
        var items = Items(6);

        // Many seeds, because the seam repeat only happens by chance.
        for (int seed = 0; seed < 300; seed++)
        {
            var playlist = new Playlist<string>(new Random(seed));

            playlist.Refill(items);
            var first = DrainOneCycle(playlist);

            playlist.Refill(items);
            var second = DrainOneCycle(playlist);

            Assert.True(
                first[^1] != second[0],
                $"seed {seed} repeated '{first[^1]}' across the cycle boundary");
        }
    }

    [Fact]
    public void ASingleItemListRepeatsBecauseItHasNoAlternative()
    {
        var playlist = new Playlist<string>(new Random(5));

        playlist.Refill(new[] { "only" });
        Assert.True(playlist.TryNext(out var a));
        playlist.Refill(new[] { "only" });
        Assert.True(playlist.TryNext(out var b));

        Assert.Equal("only", a);
        Assert.Equal("only", b);
    }

    [Fact]
    public void AnEmptyListingLeavesThePlaylistEmpty()
    {
        var playlist = new Playlist<string>(new Random(6));
        playlist.Refill(Items(3));

        playlist.Refill(Array.Empty<string>());

        Assert.True(playlist.IsEmpty);
        Assert.False(playlist.TryNext(out _));
    }

    [Fact]
    public void ShufflingIsDeterministicForAGivenSeed()
    {
        var items = Items(20);

        var a = new Playlist<string>(new Random(42));
        a.Refill(items);
        var b = new Playlist<string>(new Random(42));
        b.Refill(items);

        Assert.Equal(DrainOneCycle(a), DrainOneCycle(b));
    }

    [Fact]
    public void EveryItemIsServedOnceAcrossManyCycles()
    {
        var items = Items(8);
        var playlist = new Playlist<string>(new Random(9));
        var counts = items.ToDictionary(i => i, _ => 0);

        for (int cycle = 0; cycle < 40; cycle++)
        {
            playlist.Refill(items);
            foreach (var item in DrainOneCycle(playlist)) counts[item]++;
        }

        Assert.All(counts.Values, c => Assert.Equal(40, c));
    }
}
