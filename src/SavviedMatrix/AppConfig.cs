using System.Text.Json;
using System.Text.Json.Serialization;

namespace SavviedMatrix;

public sealed class DropboxConfig
{
    public string AppKey { get; set; } = "";
    public string Folder { get; set; } = "";
}

public sealed class CacheConfig
{
    public int MaxFiles { get; set; } = 200;
}

public sealed class RainConfig
{
    public bool Enabled { get; set; }
    public int Fps { get; set; } = 24;

    /// <summary>How long one rain pass takes to carry one image off and the next one in.</summary>
    public double? Seconds { get; set; }

    /// <summary>Superseded by <see cref="Seconds"/>; still read so older configs keep working.</summary>
    public double? InSeconds { get; set; }

    public double TransitionSeconds => Seconds ?? InSeconds ?? 3.0;
}

public sealed class SoundConfig
{
    public bool Enabled { get; set; } = true;
    public float Volume { get; set; } = 0.9f;
    public int Bands { get; set; } = 16;
    public int Polyphony { get; set; } = 8;
    public string Scale { get; set; } = "major";
    public bool StaticStrum { get; set; } = true;
    public int LatencyMs { get; set; } = 60;
}

public sealed class AppConfig
{
    public DropboxConfig Dropbox { get; set; } = new();
    public double DisplaySeconds { get; set; } = 10.0;
    public int Columns { get; set; } = 320;
    public string GlyphMode { get; set; } = "dense";
    public string Palette { get; set; } = "ansi";
    public double Gamma { get; set; } = 1.0;

    /// <summary>
    /// Local contrast, pulling each cell away from its neighbourhood average.
    ///
    /// Off by default because measurement says so: on photographs it clamps cells onto the
    /// first and last character faster than it reveals structure, and every setting above
    /// zero carried less detail than zero did, even with equalisation afterwards to clean
    /// up. Kept as a lever because some flat, evenly lit sources do benefit.
    /// </summary>
    public double Detail { get; set; } = 0.0;

    /// <summary>
    /// How far to redistribute the image's tones so each glyph level carries a similar
    /// number of cells. 0 keeps the linear stretch; 1 fully equalises. 0.8 measured best
    /// across a set of photographs; 1.0 is very slightly worse and starts to look processed.
    /// </summary>
    public double Equalize { get; set; } = 0.8;

    /// <summary>
    /// How hard to normalise each image's saturation before quantising to sixteen colours.
    /// 0 uses the image's own saturation, 1 lifts its most colourful cells to nearly full.
    /// Genuinely monochrome images are left alone at any setting.
    /// </summary>
    public double ColorBoost { get; set; } = 1.0;

    /// <summary>
    /// Ordered dithering of the colour thresholds. Sixteen colours with hard thresholds
    /// band a smooth gradient into flat patches; a small position-dependent nudge turns
    /// those edges into a stipple the eye blends. 0 disables it.
    /// </summary>
    public double Dither { get; set; } = 0.15;
    public CacheConfig Cache { get; set; } = new();
    public RainConfig Rain { get; set; } = new();
    public SoundConfig Sound { get; set; } = new();

    // Not persisted: set from the command line only.
    [JsonIgnore] public string? LocalFolder { get; set; }
    [JsonIgnore] public Size? WindowSize { get; set; }
    [JsonIgnore] public bool AuthMode { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
        {
            Log.Warn("No config.json beside the executable; using defaults.");
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Warn($"config.json could not be read ({ex.Message}); using defaults.");
            return new AppConfig();
        }
    }

    /// <summary>
    /// Applies command line overrides. Returns false with a message when the arguments
    /// are malformed, so the caller can show usage and exit.
    /// </summary>
    public bool ApplyArgs(string[] args, out string? error)
    {
        error = ParseArgs(args);
        return error is null;
    }

    /// <summary>Returns null on success, or the first problem found.</summary>
    private string? ParseArgs(string[] args)
    {
        string? err = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();

            // Captures err, which is why this is a plain local and not the out parameter.
            string? Next(string name)
            {
                if (i + 1 >= args.Length)
                {
                    err = $"{name} needs a value.";
                    return null;
                }
                return args[++i];
            }

            switch (arg)
            {
                case "--auth":
                    AuthMode = true;
                    break;

                case "--folder":
                {
                    var v = Next("--folder");
                    if (v is null) return err;
                    LocalFolder = v;
                    break;
                }

                case "--size":
                {
                    var v = Next("--size");
                    if (v is null) return err;

                    var parts = v.ToLowerInvariant().Split('x');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], out int w)
                        || !int.TryParse(parts[1], out int h)
                        || w <= 0 || h <= 0)
                    {
                        return $"--size expects WxH, for example 1920x1080 (got '{v}').";
                    }

                    WindowSize = new Size(w, h);
                    break;
                }

                case "--seconds":
                {
                    var v = Next("--seconds");
                    if (v is null) return err;
                    if (!double.TryParse(v, out double s) || s <= 0)
                        return $"--seconds expects a positive number (got '{v}').";
                    DisplaySeconds = s;
                    break;
                }

                case "--equalize":
                {
                    var v = Next("--equalize");
                    if (v is null) return err;
                    if (!double.TryParse(v, out double eq) || eq < 0 || eq > 1)
                        return $"--equalize expects 0 to 1 (got '{v}').";
                    Equalize = eq;
                    break;
                }

                case "--dither":
                {
                    var v = Next("--dither");
                    if (v is null) return err;
                    if (!double.TryParse(v, out double dz) || dz < 0 || dz > 1)
                        return $"--dither expects 0 to 1 (got '{v}').";
                    Dither = dz;
                    break;
                }

                case "--detail":
                {
                    var v = Next("--detail");
                    if (v is null) return err;
                    if (!double.TryParse(v, out double d) || d < 0 || d > 3)
                        return $"--detail expects 0 to 3 (got '{v}').";
                    Detail = d;
                    break;
                }

                case "--color-boost":
                {
                    var v = Next("--color-boost");
                    if (v is null) return err;
                    if (!double.TryParse(v, out double cb) || cb < 0 || cb > 1)
                        return $"--color-boost expects 0 to 1 (got '{v}').";
                    ColorBoost = cb;
                    break;
                }

                case "--columns":
                {
                    var v = Next("--columns");
                    if (v is null) return err;
                    if (!int.TryParse(v, out int c) || c < 16)
                        return $"--columns expects an integer of at least 16 (got '{v}').";
                    Columns = c;
                    break;
                }

                case "--mode":
                {
                    var v = Next("--mode");
                    if (v is null) return err;
                    var m = v.ToLowerInvariant();
                    if (m is not ("ramp" or "dense" or "blocks" or "katakana"))
                        return $"--mode expects ramp, dense, blocks or katakana (got '{v}').";
                    GlyphMode = m;
                    break;
                }

                case "--palette":
                {
                    var v = Next("--palette");
                    if (v is null) return err;
                    var m = v.ToLowerInvariant();
                    if (m is not ("ansi" or "matrix"))
                        return $"--palette expects ansi or matrix (got '{v}').";
                    Palette = m;
                    break;
                }

                case "--rain-seconds":
                {
                    var v = Next("--rain-seconds");
                    if (v is null) return err;
                    if (!double.TryParse(v, out double rs) || rs <= 0)
                        return $"--rain-seconds expects a positive number (got '{v}').";
                    Rain.Seconds = rs;
                    break;
                }

                case "--rain":
                    Rain.Enabled = true;
                    break;

                case "--no-rain":
                    Rain.Enabled = false;
                    break;

                case "--fps":
                {
                    var v = Next("--fps");
                    if (v is null) return err;
                    if (!int.TryParse(v, out int f) || f < 1 || f > 120)
                        return $"--fps expects 1 to 120 (got '{v}').";
                    Rain.Fps = f;
                    break;
                }

                case "--mute":
                    Sound.Enabled = false;
                    break;

                case "--volume":
                {
                    var v = Next("--volume");
                    if (v is null) return err;
                    if (!float.TryParse(v, out float vol) || vol < 0 || vol > 1)
                        return $"--volume expects 0 to 1 (got '{v}').";
                    Sound.Volume = vol;
                    break;
                }

                case "--help":
                case "-h":
                case "/?":
                    return "";

                default:
                    return $"Unknown option '{args[i]}'.";
            }
        }

        return err;
    }

    public const string Usage = """
        SavviedMatrix - Matrix ASCII image viewer

          SavviedMatrix.exe [options]

          --auth              Run the one-time Dropbox authorization and exit.
          --folder <path>     Read images from a local directory instead of Dropbox.
          --size WxH          Run in a borderless window of this size instead of fullscreen.
          --seconds <n>       Hold each image for n seconds (default 10).
          --columns <n>       Character columns across the screen (default 320).
          --detail <0..3>     Local contrast; higher makes photographs easier to read.
          --equalize <0..1>   Tone redistribution; higher uses the glyph levels more evenly.
          --dither <0..1>     Breaks up banding in the sixteen-colour quantisation.
          --color-boost <0..1>  How hard to normalise each image's saturation.
          --mode ramp|dense|blocks|half|katakana
          --palette ansi|matrix
          --rain / --no-rain  Enable or disable the rain transition between images.
          --rain-seconds <n>  Length of one rain pass (default 3).
          --fps <n>           Rain frame rate (default 24).
          --mute              Disable sound.
          --volume <0..1>     Master volume.
        """;
}
