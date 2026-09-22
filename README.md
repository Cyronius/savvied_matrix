# SavviedMatrix

A slideshow that renders images as sixteen-colour ASCII art on a television. Images come
from a Dropbox app folder over the API, so the display machine needs no Dropbox client
installed. Optionally each image hands over to the next in a single pass of Matrix digital
rain, with synthesized plinks whose pitch comes from the colours of the image.

Runs on macOS, Windows and Linux from one codebase. Requirements live in
[specs/matrix/spec.md](specs/matrix/spec.md).

## Setting it up

You need the Dropbox app key once, from an app you create at
https://www.dropbox.com/developers/apps with **Scoped access**, **App folder**, and the
`files.metadata.read` and `files.content.read` permissions. Set the permissions before you
authorize, or the token will be issued without them.

1. Put the key in `config.json` (see [Where the files live](#where-the-files-live)), under
   `dropbox.appKey`.
2. Run `SavviedMatrix --auth` on any machine you can use a browser on. Click Allow, paste
   the code into the dialog. This writes `token.json` into the data directory.
3. Copy `config.json` and `token.json` to the display machine. The refresh token is not tied
   to a machine, so the kiosk never needs a browser.
4. Put images in `Dropbox/Apps/<your app name>/`. They appear at the next cycle without a
   restart.

`token.json` grants read access to that one app folder. Do not commit it.

## Running it

Double-click the application. Any key or mouse click exits. It never exits on its own: a bad
image is skipped, a dropped network falls back to the cache, and an empty folder shows a
message and keeps checking.

**macOS.** Install it once and it looks after itself:

```
./build/package-mac.sh            # dist/SavviedMatrix.app
./build/install-mac.sh            # to /Applications, starts at login
```

The bundle carries its own .NET runtime, so the display machine needs nothing installed,
and neither step needs sudo.

The installer adds a launch agent at `~/Library/LaunchAgents/com.savvied.matrix.plist`
that starts the app at login and restarts it if it crashes. It deliberately does *not*
restart it after a normal exit: any key or click quits the app and returns success, so the
kiosk stays closed when someone means to close it and you are never locked out of the
machine. To start it again without logging out:

```
launchctl kickstart gui/$(id -u)/com.savvied.matrix
```

To remove the app and the agent, keeping your settings and token:

```
./build/install-mac.sh uninstall
```

### Starting without anyone logging in

The launch agent starts the app at *login*, so after a power cut the kiosk waits at the
login window. Two settings finish the job, both needing an administrator:

```
sudo pmset -a autorestart 1       # power comes back, the Mac comes back
```

and automatic login, under System Settings, Users & Groups, Automatic login. Note what
that trades away: anyone who reboots the machine gets a logged-in session, and macOS
stores the password in a recoverable form to do it. Reasonable for a wall display on a
dedicated account, not for a machine with anything else on it. It also requires FileVault
to be off.

### If the picture looks cropped on the television

Check the set's picture-size setting first. Most televisions overscan by three to five per
cent unless told not to; the option is usually called Just Scan, Screen Fit, 1:1 or Full.
The log says what the app actually drew:

```
Presenting 1920x1080px into 1920x1080 at 1x as 1920x1080px at 0,0 (1:1)
```

If that line says anything other than `(1:1)` at offset `0,0`, the app is at fault. If it
says 1:1 and the edges are still missing, the television is.

**Windows.** Put a shortcut in `shell:startup` to start it with Windows. SmartScreen will
warn once about an unsigned executable: choose More info, then Run anyway.

## Where the files live

`config.json`, `token.json`, the image `cache/` and `SavviedMatrix.log` sit beside the
executable, which is what makes the app a folder you can copy to a kiosk.

macOS is the exception. An application there is a bundle and a bundle is meant to be
read-only, so a bundled build keeps all four in
`~/Library/Application Support/SavviedMatrix/` instead. A starting `config.json` is copied
there on first run. This is also where to look for the log:

```
~/Library/Application Support/SavviedMatrix/SavviedMatrix.log
```

Running from the source tree with `dotnet run` uses the build output directory on every
platform, so development is unaffected.

## Configuration

Command line options override `config.json`.

| Setting | Default | What it does |
|---|---|---|
| `dropbox.folder` | `""` | Subfolder inside the app folder; empty is the root |
| `displaySeconds` | 10 | How long each image is held |
| `columns` | 320 | Characters across the screen; more is finer, 480 is about the limit |
| `glyphMode` | `dense` | `ramp`, `dense`, `blocks`, `half` or `katakana`; see below |
| `palette` | `ansi` | `ansi` for sixteen terminal colours, `matrix` for sixteen greens |
| `equalize` | 0.8 | Spreads tones evenly over the character levels; the main legibility knob |
| `detail` | 0.0 | Local contrast; measured worse than off on photographs, see below |
| `colorBoost` | 1.0 | Normalises each image's saturation before the sixteen-colour match |
| `dither` | 0.15 | Stipples the colour thresholds so gradients do not band |
| `gamma` | 1.0 | Above 1 darkens the mid-tones |
| `cache.maxFiles` | 200 | Cached images kept on disk, least-recently-used evicted |
| `rain.enabled` | false | The rain handover between images |
| `rain.fps` | 24 | Rain frame rate |
| `rain.seconds` | 3 | Length of one rain pass |
| `sound.enabled` | true | The synthesizer |
| `sound.volume` | 0.9 | Master volume; typical content peaks near 0.8 before the limiter |
| `sound.bands` | 16 | Plinks per rain phase; set equal to `columns` for chaos |
| `sound.scale` | `major` | `major` or `pentatonic` |
| `sound.latencyMs` | 60 | Raise if the plink trails the visual, lower if it leads |

Options: `--auth`, `--folder <path>`, `--size WxH`, `--seconds <n>`, `--columns <n>`,
`--mode ramp|dense|blocks|katakana`, `--palette ansi|matrix`, `--equalize <0..1>`,
`--detail <0..3>`, `--color-boost <0..1>`, `--dither <0..1>`, `--rain`, `--no-rain`,
`--rain-seconds <n>`, `--fps <n>`, `--mute`, `--volume <0..1>`.

## Making photographs readable

There are only about thirty distinguishable characters, so what matters is not the range an
image spans but how its tones are spread across those levels. A photograph that is two
thirds dark background spends two thirds of its cells on the bottom few characters, which is
the difference between a picture and a silhouette.

`equalize` is the knob that fixes this, and it is the first one to reach for. Measured over
a set of photographs, moving it from 0 to 0.8 raised the detail carried per cell from 3.03
to 4.39 bits and cut neutral-coloured cells from 6.8 per cent to 3.1. Above 0.8 it gets
very slightly worse and starts to look processed.

`colorBoost` normalises each image's saturation before it is quantised, which is what stops
muted photographs coming out grey. Genuinely monochrome images are detected and left alone.

`detail` adds local contrast. It is off by default because it measured worse than doing
nothing on every photograph tested: it clamps cells onto the first and last character faster
than it reveals structure. Flat, evenly lit sources may still benefit, so the lever is there.

`columns` is the blunt instrument. More columns means more cells and more total detail, at
the cost of smaller characters. 320 is the default and measured best for detail carried per
cell. Above about 384 the characters stop reading as characters and the detail per cell
starts falling again, so 480 is roughly the useful limit.

`dither` stipples the colour thresholds. With only sixteen colours a smooth gradient
otherwise bands into flat patches with hard edges; the stipple blends them.

The ramps themselves are rebuilt at startup from the ink each character actually puts on
screen in the font and cell size in use, rather than from a conventional ordering, so
brightness steps are even instead of banded. Tinting is normalised against the densest
glyph available, which is what stops the whole picture dimming as the cells get smaller.

**If you want maximum detail and do not mind losing the text**, set `glyphMode` to `half`.
Each cell then draws no character at all: it is split into a coloured top and bottom half,
which doubles the vertical resolution and makes the effective pixels square. At 320 columns
that is 57,600 samples against 28,800, and at 480 columns it is 129,600. The rain still
falls in characters over the top.

## How it looks

Two things control the look, and they are independent.

**Palette.** `ansi` quantises each cell to the nearest of the sixteen standard terminal
colours, so the image keeps its own hues and the result reads as terminal art. `matrix`
throws the hues away and uses sixteen shades of green instead. One practical difference:
under `ansi` a genuinely black region is black, exactly as it would be in a terminal, so a
very dark photograph shows very little. Under `matrix` the darkest shade is a dim green
rather than black, so dark areas still read as lit characters. If your folder is full of
moody low-key shots, try `matrix`.

**Glyphs.** `dense` uses the seventy-step ASCII-art ramp and gives the finest tonal
gradation. `ramp` is the ten-step `.:-=+*#%@`, chunkier and more legible from across a
room. `blocks` uses the Unicode shading blocks for a clean solid look. `katakana` fills the
picture with the film's characters, chosen at random, with brightness carried entirely by
the colour. The falling rain always uses katakana whatever the image mode is set to.

## The rain

With rain on, one image hands over to the next in a single pass. Ahead of each falling head
the outgoing image is still standing, the head and its trail are the rain itself, and behind
the trail the incoming image has settled. The screen is never blank between images.

Rain is off by default because it is the only part that costs work every frame. To decide
whether the display machine can take it:

```
SavviedMatrix --rain
```

Let it run through a few images, then read `SavviedMatrix.log`. Each transition logs its
average compose-and-upload time against the frame budget. On an M4 Mac mini at 1920x1080 it
measures about 1.5 ms against a 42 ms budget at 24 fps. If the average on your machine
approaches the budget, drop `rain.fps` to 15 or `columns` to 128 and measure again.

## How the sound works

Nothing is sampled. Every value is computed from a phase-accumulator oscillator, an
exponential envelope, a pitch glide and a one-pole filter. The saw and square are band
limited so they stay cheap-sounding without the fold-back grit that reads as crackle. The
library in use only carries the finished buffers to the sound card.

Hue picks the note on a ROYGBIV mapping: red is the root, then orange, yellow, green, blue,
indigo and violet up the scale. Brightness picks the octave, saturation picks the waveform
from sine through triangle and square to saw, stream speed sets the attack, trail length
sets the decay. Grey areas play the root and letterboxed bands are silent, so a monochrome
photograph is one repeated note and a colourful one is a melody.

Plinks are scheduled from the same numbers that drive the rain, at the moment each stream
reaches the bottom of the screen, so the sound stays aligned with the picture even if the
machine drops frames.

If you hear crackling, raise `sound.latencyMs`. The output is queued as four buffers of a
quarter of that value each, so a higher number gives the audio thread more slack at the cost
of the plinks landing slightly later behind the picture.

## Development

Needs the .NET 10 SDK; `global.json` pins it.

```
dotnet test                       # 266 tests
dotnet run --project src/SavviedMatrix -- --folder ./testimages --size 1600x900 --rain
```

`--folder` bypasses Dropbox entirely, and `--size` previews the exact 1920x1080 television
layout in a window, in real pixels whatever the display's scale factor is.

To build the macOS bundle:

```
./build/package-mac.sh            # dist/SavviedMatrix.app, self-contained
```

To build the Windows distributable:

```
dotnet publish src/SavviedMatrix -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

### How it stays portable

Nothing in the analysis, rain or synthesis touches a platform API; the boundary is four
things, and each is one library rather than one per platform.

| Concern | Library | Notes |
|---|---|---|
| Window, input, blitting | Avalonia | One window implementation on all three platforms |
| Image decode, resize, EXIF | SkiaSharp | Ships with Avalonia, so it costs no extra dependency |
| Glyph rasterising | SkiaSharp | Fonts are chosen per platform and then *measured*, not assumed |
| Audio | SoundFlow (miniaudio) | CoreAudio, WASAPI or ALSA underneath; native library is in the package |

The only per-platform code is display-sleep suppression, which has no portable equivalent:
`SetThreadExecutionState` on Windows and an `IOPMAssertion` on macOS.

Fonts are the one thing that genuinely differs. The app asks for Consolas on Windows, Menlo
on macOS and DejaVu Sans Mono on Linux, falling back down a list and finally to whatever the
system offers. Because the ramps are rebuilt at startup from the ink each character actually
puts on screen, a substitution changes the texture slightly and breaks nothing.

## Layout

```
src/SavviedMatrix/
  Ascii/      image to characters: fit, luminance, glyph mapping, the sixteen-colour palette
  Audio/      colour to notes, scheduling, and the synthesizer itself
  Rain/       the falling-stream model and its compositor
  Render/     the glyph atlas and the pixel compositor
  Source/     Dropbox API, cache, and the local-folder development source
  Ui/         the fullscreen window, phase machine, and display-sleep suppression
specs/matrix/ the canonical spec, its tests, and the archived plan
```
