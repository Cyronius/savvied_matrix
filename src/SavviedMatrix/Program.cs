using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
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

    /// <summary>
    /// Avalonia has to be configured before any control is touched, and the viewer needs the
    /// screen size before it can build a grid, so everything real happens inside
    /// <see cref="Run"/> once the toolkit is up.
    /// </summary>
    private static int Main(string[] args)
    {
        int exit = ExitFailed;

        // Before the toolkit, not after. A display that is already asleep is not in the
        // window server's active list, and the renderer's frame clock is built from that
        // list, so a kiosk starting on an idle machine would otherwise fail outright.
        KeepAwake.Engage();

        try
        {
            BuildAvaloniaApp().Start((_, _) => exit = Run(args), args);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal", ex);
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return ExitFailed;
        }
        finally
        {
            KeepAwake.Release();
        }

        return exit;
    }

    /// <summary>Also used by the Avalonia previewer and design tooling, which looks for this name.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static int Run(string[] args)
    {
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

        var dialog = new AuthDialog();
        RunWindow(dialog);

        if (string.IsNullOrWhiteSpace(dialog.Code))
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
                + "Copy this file to the same folder on the display machine.",
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
                    + "Run SavviedMatrix --auth once, then copy token.json to:\n\n"
                    + $"{AppPaths.DataDirectory}",
                    "Not authorized");
                return ExitNoToken;
            }

            http = new HttpClient();
            var client = new DropboxClient(http, config.Dropbox.AppKey, token);
            var cache = new ImageCache(AppPaths.Combine("cache"), config.Cache.MaxFiles);

            source = new DropboxSource(client, cache, config.Dropbox.Folder);
            Log.Info($"Reading images from {source.Description}.");
        }

        try
        {
            var (screenWidth, screenHeight, scaling) = ScreenInfo.Primary();

            int width = config.WindowSize?.Width ?? screenWidth;
            int height = config.WindowSize?.Height ?? screenHeight;

            var geometry = new GridGeometry(width, height, config.Columns);
            var glyphs = new GlyphSet();
            var palette = new Palette(Palette.ParseMode(config.Palette));

            Log.Info(
                $"Screen {screenWidth}x{screenHeight} at {scaling:0.##}x, "
                + $"surface {width}x{height}, "
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

            var window = new MatrixWindow(
                config, geometry, glyphs, atlas, palette, source, audio, new Random());

            RunWindow(window);

            return ExitOk;
        }
        finally
        {
            http?.Dispose();
        }
    }

    /// <summary>
    /// Shows a window and pumps the dispatcher until it closes, which is what running a
    /// window to completion means now that the toolkit no longer offers it directly. Used
    /// for the viewer and for each dialog in turn, so the whole app is a sequence of
    /// windows rather than a lifetime object with one main window.
    /// </summary>
    private static void RunWindow(Window window)
    {
        using var cts = new CancellationTokenSource();

        window.Closed += (_, _) => cts.Cancel();
        window.Show();

        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    /// <summary>
    /// Shows a message and waits for it to be dismissed. Also written to the log, so a
    /// headless or unattended run still records why it stopped.
    /// </summary>
    private static void Message(string text, string caption)
    {
        Log.Info($"{caption}: {text.ReplaceLineEndings(" ")}");
        Console.WriteLine($"{caption}\n\n{text}\n");

        try
        {
            RunWindow(new MessageWindow(text, caption));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not show the message window: {ex.Message}");
        }
    }
}
