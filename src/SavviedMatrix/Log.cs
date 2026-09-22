namespace SavviedMatrix;

/// <summary>
/// Append-only log in the app's data directory. Deliberately dumb: the kiosk runs
/// unattended, and this is the only way to find out afterwards what it did.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = AppPaths.Combine("SavviedMatrix.log");

    /// <summary>Where the log is being written, for the startup banner.</summary>
    public static string FilePath => Path;

    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex)
        => Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}";
        try
        {
            lock (Gate)
            {
                RollIfLarge();
                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch
        {
            // A kiosk that cannot write its log still has to keep showing pictures.
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    private static void RollIfLarge()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length < MaxBytes) return;

            var old = Path + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(Path, old);
        }
        catch
        {
            // Ignore: rolling is a nicety.
        }
    }
}
