using SavviedMatrix.Render;

namespace SavviedMatrix.Tests;

/// <summary>
/// Command line parsing, for the options where the parser and the rest of the app could
/// drift apart without anything failing to build.
/// </summary>
public class AppConfigArgsTests
{
    private static AppConfig Parse(params string[] args)
    {
        var config = new AppConfig();
        Assert.True(config.ApplyArgs(args, out var error), error);
        return config;
    }

    /// <summary>
    /// Every mode the glyph set understands has to survive the parser. These two lists
    /// were out of step once: half-block was reachable from config.json and rejected on
    /// the command line, while the usage text offered it.
    /// </summary>
    [Theory]
    [InlineData("ramp", GlyphMode.Ramp)]
    [InlineData("dense", GlyphMode.Dense)]
    [InlineData("blocks", GlyphMode.Blocks)]
    [InlineData("katakana", GlyphMode.Katakana)]
    [InlineData("half", GlyphMode.HalfBlock)]
    [InlineData("halfblock", GlyphMode.HalfBlock)]
    [InlineData("half-block", GlyphMode.HalfBlock)]
    public void ModeArgumentAcceptsEveryGlyphMode(string argument, GlyphMode expected)
    {
        var config = Parse("--mode", argument);
        Assert.Equal(expected, GlyphSet.ParseMode(config.GlyphMode));
    }

    [Fact]
    public void AnUnknownModeIsRejectedAndNamedInTheMessage()
    {
        var config = new AppConfig();
        Assert.False(config.ApplyArgs(new[] { "--mode", "sideways" }, out var error));
        Assert.Contains("sideways", error);
    }

    [Fact]
    public void EveryModeTheUsageTextOffersIsAccepted()
    {
        // The usage line reads "--mode ramp|dense|blocks|half|katakana".
        var line = AppConfig.Usage
            .Split('\n')
            .Single(l => l.Contains("--mode "));

        var offered = line[(line.IndexOf("--mode ", StringComparison.Ordinal) + 7)..]
            .Trim()
            .Split('|');

        foreach (var mode in offered)
        {
            var config = new AppConfig();
            Assert.True(
                config.ApplyArgs(new[] { "--mode", mode }, out var error),
                $"usage offers '{mode}' but the parser rejected it: {error}");
        }
    }
}
