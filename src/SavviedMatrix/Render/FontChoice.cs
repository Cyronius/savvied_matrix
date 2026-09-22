using System.Runtime.InteropServices;
using SkiaSharp;

namespace SavviedMatrix.Render;

/// <summary>
/// Picks the two typefaces the atlas draws with: a monospace face for the ASCII ramps and
/// a face that carries half-width katakana for the rain.
///
/// The names differ per platform and nothing is bundled, so each is a preference list ending
/// in a character-based lookup. What matters for the picture is only that the face is
/// monospace and dense; the atlas measures the ink it actually gets rather than assuming
/// anything about the font, so a substitution changes the look a little and breaks nothing.
/// </summary>
public static class FontChoice
{
    /// <summary>A character from the half-width katakana block, used to test a candidate face.</summary>
    public const char KatakanaSample = 'ﾊ';

    private static readonly string[] MonospaceWindows =
        { "Consolas", "Cascadia Mono", "Lucida Console", "Courier New" };

    private static readonly string[] MonospaceMac =
        { "Menlo", "SF Mono", "Monaco", "Courier New" };

    private static readonly string[] MonospaceLinux =
        { "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Ubuntu Mono", "FreeMono" };

    private static readonly string[] KatakanaWindows =
        { "MS Gothic", "Yu Gothic", "Meiryo", "Segoe UI" };

    // Hiragino is the system Japanese face on macOS and carries the half-width block.
    // Menlo is listed last because it covers the block too, just less evenly.
    private static readonly string[] KatakanaMac =
        { "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Apple SD Gothic Neo", "Menlo" };

    private static readonly string[] KatakanaLinux =
        { "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP", "IPAGothic", "VL Gothic" };

    /// <summary>The monospace face for the ramps, never null: Skia's default is the last resort.</summary>
    public static SKTypeface Monospace()
    {
        var names = OperatingSystem.IsWindows() ? MonospaceWindows
            : OperatingSystem.IsMacOS() ? MonospaceMac
            : MonospaceLinux;

        var chosen = FirstAvailable(names);
        if (chosen is not null) return chosen;

        // Ask the platform for whatever it considers fixed-pitch before giving up.
        var generic = SKTypeface.FromFamilyName(
            "monospace", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);

        if (generic is not null)
        {
            Log.Warn($"No preferred monospace font found; using '{generic.FamilyName}'.");
            return generic;
        }

        Log.Warn("No monospace font found; falling back to the default typeface.");
        return SKTypeface.Default;
    }

    /// <summary>
    /// A face that actually contains <see cref="KatakanaSample"/>. A face that merely exists
    /// is not enough: a missing glyph renders as a blank or a box, which would put holes in
    /// the rain rather than characters.
    /// </summary>
    public static SKTypeface Katakana()
    {
        var names = OperatingSystem.IsWindows() ? KatakanaWindows
            : OperatingSystem.IsMacOS() ? KatakanaMac
            : KatakanaLinux;

        foreach (var name in names)
        {
            var face = SKTypeface.FromFamilyName(name);
            if (face is null) continue;

            if (Covers(face, KatakanaSample)) return face;
            face.Dispose();
        }

        // Nothing on the preference list has the block; let the platform's font manager
        // find any installed face that does.
        var matched = SKFontManager.Default.MatchCharacter(KatakanaSample);
        if (matched is not null)
        {
            Log.Info($"Using '{matched.FamilyName}' for katakana.");
            return matched;
        }

        Log.Warn(
            "No installed font carries half-width katakana; the rain will fall in "
            + "whatever the default typeface substitutes.");

        return SKTypeface.Default;
    }

    /// <summary>True when the face has a real glyph for the character.</summary>
    private static bool Covers(SKTypeface face, char c)
    {
        // GetGlyph returns 0 for .notdef, which is what a missing character maps to.
        try
        {
            using var font = new SKFont(face);
            return font.GetGlyph(c) != 0;
        }
        catch { return false; }
    }

    private static SKTypeface? FirstAvailable(string[] names)
    {
        foreach (var name in names)
        {
            var face = SKTypeface.FromFamilyName(name);
            if (face is null) continue;

            // Skia substitutes silently, so confirm we got the family we asked for
            // rather than the default wearing its name.
            if (string.Equals(face.FamilyName, name, StringComparison.OrdinalIgnoreCase))
                return face;

            face.Dispose();
        }

        return null;
    }

    /// <summary>A one-line description of the platform and the faces in use, for the log.</summary>
    public static string Describe(SKTypeface mono, SKTypeface katakana)
        => $"{RuntimeInformation.OSDescription.Split('#')[0].Trim()} "
         + $"({RuntimeInformation.ProcessArchitecture}), "
         + $"ramp font '{mono.FamilyName}', katakana font '{katakana.FamilyName}'";
}
