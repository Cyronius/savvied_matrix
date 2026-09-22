namespace SavviedMatrix;

/// <summary>
/// Where the app keeps the things it reads and writes: the config, the Dropbox token, the
/// image cache and the log.
/// <para>
/// On Windows and Linux that is simply the directory the executable sits in, which is what
/// makes the app a folder you can copy to a kiosk. macOS is the exception: an application is
/// a bundle, and a bundle is meant to be read-only. Writing a growing cache and a log inside
/// one survives until the day it is replaced or signed, and then the token goes with it and
/// the kiosk silently stops having access to the photographs. So a bundled build keeps its
/// state in Application Support instead, the way every other Mac application does.
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>The directory the executable was loaded from.</summary>
    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>True when running from inside a macOS .app bundle rather than a plain folder.</summary>
    public static bool InsideAppBundle { get; } =
        OperatingSystem.IsMacOS()
        && AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Contents/MacOS/", StringComparison.Ordinal);

    /// <summary>
    /// The writable directory for config, token, cache and log. Beside the executable
    /// everywhere except inside a macOS bundle.
    /// </summary>
    public static string DataDirectory { get; } = Resolve();

    public static string Combine(string name) => Path.Combine(DataDirectory, name);

    /// <summary>
    /// The read-only copy of a file that ships beside the executable, used as the default
    /// for a bundled build's first run.
    /// </summary>
    public static string Installed(string name) => Path.Combine(InstallDirectory, name);

    private static string Resolve()
    {
        if (!InsideAppBundle) return AppContext.BaseDirectory;

        try
        {
            // Built explicitly rather than through SpecialFolder, which maps to the
            // XDG-style ~/.config on this platform and would hide the folder from the
            // operator in a place no Mac user looks.
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "SavviedMatrix");

            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            // No logging here: the log itself is resolved through this, so falling back
            // quietly is the only option that cannot recurse.
            return AppContext.BaseDirectory;
        }
    }
}
