// Traces: MATRIX-CACHE
using SavviedMatrix.Source.Dropbox;

namespace SavviedMatrix.Tests;

/// <summary>
/// Covers <see cref="ImageCache.SelectForEviction"/>, the pure half of the cache policy.
/// The filesystem half (deleting, touching) is verified manually.
/// </summary>
public class ImageCacheEvictionTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (string Path, DateTime LastWrite) Entry(string name, int minutesOld)
        => (name, Epoch.AddMinutes(-minutesOld));

    [Fact]
    public void EmptyInput_SelectsNothing()
    {
        var selected = ImageCache.SelectForEviction(Array.Empty<(string, DateTime)>(), 10);

        Assert.Empty(selected);
    }

    [Fact]
    public void UnderTheCap_SelectsNothing()
    {
        var files = new[] { Entry("a.jpg", 30), Entry("b.jpg", 20), Entry("c.jpg", 10) };

        var selected = ImageCache.SelectForEviction(files, 5);

        Assert.Empty(selected);
    }

    [Fact]
    public void ExactlyAtTheCap_SelectsNothing()
    {
        var files = new[] { Entry("a.jpg", 30), Entry("b.jpg", 20), Entry("c.jpg", 10) };

        var selected = ImageCache.SelectForEviction(files, 3);

        Assert.Empty(selected);
    }

    [Fact]
    public void OverTheCap_SelectsExactlyTheOverflowCount()
    {
        var files = Enumerable.Range(0, 10)
            .Select(i => Entry($"file{i:00}.jpg", i))
            .ToArray();

        var selected = ImageCache.SelectForEviction(files, 4);

        Assert.Equal(6, selected.Count);
    }

    [Fact]
    public void SelectsTheOldestFiles_OldestFirst()
    {
        // newest -> oldest: fresh (1), middle (5), stale (20), ancient (99)
        var files = new[]
        {
            Entry("fresh.jpg", 1),
            Entry("ancient.jpg", 99),
            Entry("middle.jpg", 5),
            Entry("stale.jpg", 20)
        };

        var selected = ImageCache.SelectForEviction(files, 2);

        Assert.Equal(new[] { "ancient.jpg", "stale.jpg" }, selected);
    }

    [Fact]
    public void KeepsTheNewestFile_WhenEverythingElseGoes()
    {
        var files = new[] { Entry("a.jpg", 3), Entry("b.jpg", 2), Entry("c.jpg", 1) };

        var selected = ImageCache.SelectForEviction(files, 1);

        Assert.Equal(new[] { "a.jpg", "b.jpg" }, selected);
        Assert.DoesNotContain("c.jpg", selected);
    }

    [Fact]
    public void IdenticalTimestamps_BreakTiesOnPathOrdinal()
    {
        var sameInstant = Epoch.AddMinutes(-7);
        var files = new[]
        {
            ("zulu.jpg", sameInstant),
            ("alpha.jpg", sameInstant),
            ("mike.jpg", sameInstant)
        };

        var selected = ImageCache.SelectForEviction(files, 1);

        Assert.Equal(new[] { "alpha.jpg", "mike.jpg" }, selected);
    }

    [Fact]
    public void EnumerationOrderDoesNotChangeTheResult()
    {
        var sameInstant = Epoch.AddMinutes(-7);
        var forwards = new[]
        {
            ("alpha.jpg", sameInstant),
            ("mike.jpg", sameInstant),
            ("zulu.jpg", Epoch.AddMinutes(-1))
        };
        var backwards = forwards.Reverse().ToArray();

        Assert.Equal(
            ImageCache.SelectForEviction(forwards, 1),
            ImageCache.SelectForEviction(backwards, 1));
    }

    [Fact]
    public void MaxFilesOfZero_SelectsEverything()
    {
        var files = new[] { Entry("a.jpg", 3), Entry("b.jpg", 1), Entry("c.jpg", 2) };

        var selected = ImageCache.SelectForEviction(files, 0);

        Assert.Equal(new[] { "a.jpg", "c.jpg", "b.jpg" }, selected);
    }

    [Fact]
    public void NegativeMaxFiles_IsTreatedAsZero()
    {
        var files = new[] { Entry("a.jpg", 2), Entry("b.jpg", 1) };

        var selected = ImageCache.SelectForEviction(files, -5);

        Assert.Equal(new[] { "a.jpg", "b.jpg" }, selected);
    }
}
