using System.Diagnostics;
using SavviedMatrix.Ascii;
using SavviedMatrix.Audio;
using SavviedMatrix.Core;
using SavviedMatrix.Render;
using SavviedMatrix.Source;
using SavviedMatrix.Source.Dropbox;
using SavviedMatrix.Ui;

namespace SavviedMatrix;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitNoToken = 2;
    private const int ExitFailed = 3;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var config = AppConfig.Load();

        if (!config.ApplyArgs(args, out var error))
        {
            if (!string.IsNullOrEmpty(error))
            {
                Log.Error($"Bad arguments: {error}");
                Message($"{error}\n\n{AppConfig.Usage}", "Bad arguments");
                return ExitUsage;
            }

            Message(AppConfig.Usage, "SavviedMatrix");
            return ExitOk;
        }

        try
        {
            return config.AuthMode ? RunAuth(config) : RunViewer(config);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            Message($"{ex.GetType().Name}: {ex.Message}", "SavviedMatrix failed");
            return ExitFailed;
        }
        finally
        {
            KeepAwake.Release();
        }
    }

    // ---------------------------------------------------------------- authorization

    private static int RunAuth(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Dropbox.AppKey)
            || config.Dropbox.AppKey.StartsWith("PASTE", StringComparison.OrdinalIgnoreCase))
        {
            Message(
                $"Set dropbox.appKey in {AppConfig.ConfigPath} first.\n\n"
                + "Create the app at https://www.dropbox.com/developers/apps with "
                + "Scoped access, App folder, and the files.metadata.read and "
                + "files.content.read permissions.",
                "No app key");
            return ExitUsage;
        }

        var verifier = DropboxAuth.GenerateCodeVerifier();
        var challenge = DropboxAuth.CodeChallenge(verifier);
        var url = DropboxAuth.BuildAuthorizeUrl(config.Dropbox.AppKey, challenge);

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open the browser: {ex.Message}");
            Message($"Open this address manually:\n\n{url}", "Dropbox authorization");
        }

        using var dialog = new AuthDialog();
        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.Code))
        {
            Log.Info("Authorization cancelled.");
            return ExitUsage;
        }

        using var http = new HttpClient();

        try
        {
            var token = DropboxAuth
                .ExchangeCodeAsync(http, config.Dropbox.AppKey, dialog.Code.Trim(), verifier, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            DropboxTokenStore.Save(token);

            Log.Info("Dropbox authorization saved.");
            Message(
                $"Saved to {DropboxTokenStore.TokenPath}.\n\n"
                + "Copy this file to the display PC alongside the executable.",
                "Authorized");

            return ExitOk;
        }
        catch (Exception ex)
        {
            Log.Error("Token exchange failed", ex);
            Message($"Dropbox rejected the code.\n\n{ex.Message}", "Authorization failed");
            return ExitFailed;
        }
    }

    // ---------------------------------------------------------------- viewer

    private static int RunViewer(AppConfig config)
    {
        HttpClient? http = null;
        IImageSource source;

        if (config.LocalFolder is { } folder)
        {
            Log.Info($"Reading images from the local folder {folder}.");
            source = new LocalFolderSource(folder);
        }
        else
        {
            var token = DropboxTokenStore.Load();
            if (token is null)
            {
                Log.Error($"No token.json at {DropboxTokenStore.TokenPath}; run --auth first.");
                Message(
                    "No Dropbox authorization found.\n\n"
                    + "Run SavviedMatrix.exe --auth once, then copy token.json next to the "
                    + "executable on this machine.",
                    "Not authorized");
                return ExitNoToken;
            }

            http = new HttpClient();
            var client = new DropboxClient(http, config.Dropbox.AppKey, token);
            var cache = new ImageCache(
                Path.Combine(AppContext.BaseDirectory, "cache"),
                config.Cache.MaxFiles);

            source = new DropboxSource(client, cache, config.Dropbox.Folder);
            Log.Info($"Reading images from {source.Description}.");
        }

        try
        {
            var screen = config.WindowSize
                ?? Screen.PrimaryScreen?.Bounds.Size
                ?? new Size(1920, 1080);

            var geometry = new GridGeometry(screen.Width, screen.Height, config.Columns);
            var glyphs = new GlyphSet();
            var palette = new Palette(Palette.ParseMode(config.Palette));

            Log.Info(
                $"Screen {screen.Width}x{screen.Height}, "
                + $"grid {geometry.Cols}x{geometry.Rows}, "
                + $"cells {geometry.CellWidth}x{geometry.CellHeight}px, "
                + $"{config.GlyphMode} glyphs, {palette.Mode.ToString().ToLowerInvariant()} palette, "
                + $"detail {config.Detail:0.##}, equalize {config.Equalize:0.##}, "
                + $"colour boost {config.ColorBoost:0.##}, "
                + $"rain {(config.Rain.Enabled ? "on" : "off")}, "
                + $"sound {(config.Sound.Enabled ? "on" : "off")}.");

            var atlas = new GlyphAtlas(glyphs, palette, geometry.CellWidth, geometry.CellHeight);

            // Rebuild the ramps from what the font actually draws at this cell size, so a
            // step up in brightness is a step up in ink rather than in convention.
            glyphs.Calibrate(atlas.Coverage, atlas.CentroidY);
            var audio = AudioOutput.Create(config.Sound);

            using var form = new MatrixForm(
                config, geometry, glyphs, atlas, palette, source, audio, new Random());

            KeepAwake.Engage();
            Application.Run(form);

            return ExitOk;
        }
        finally
        {
            http?.Dispose();
        }
    }

    private static void Message(string text, string caption)
        => MessageBox.Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
}
