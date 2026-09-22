# Matrix ASCII Viewer — Plan

**Status:** draft v5, awaiting review
**Date:** 2026-09-21
**Project:** `C:\code\savvied_matrix` (greenfield, not yet a git repo)

## Goal

A single Windows `.exe` that runs unattended on a 1080p TV driven by a low-power PC with no
Dropbox desktop client. It pulls images from a Dropbox app folder over the Dropbox API,
cycles through them as green-on-black ASCII art in the Matrix aesthetic, holding each for
10 seconds, until someone presses a key or clicks. Optionally each image rains in (Matrix
digital rain reveals it), holds, then rains out. Reactive cheesy-synth plinks play as rain
streams land, with pitch from the image's colors (ROYGBIV → notes) and length/shape from
the stream and the image content. Developed and tested end-to-end on this PC.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Language / runtime | C# .NET 10, WinForms, DPI-aware (`PerMonitorV2`) | Native Windows exe, no PyInstaller antivirus false positives, full control of a fullscreen window. DPI awareness so the app sees physical pixels (this PC is 1920×1200 at 125% scaling; the TV is 1080p at 100%). |
| Image source | **Dropbox API, App-folder scoped, OAuth 2 PKCE with a refresh token.** Raw `HttpClient` + `System.Text.Json`, no SDK. | TV PC has no Dropbox desktop. App-folder access means the token can only ever see `Dropbox/Apps/<AppName>/`, not the SVI team space. PKCE means no app secret ships in the exe. Three endpoints don't justify a NuGet dependency. |
| What gets downloaded | `files/get_thumbnail_v2` at `w1024h768`, JPEG. Fallback to `files/download` if the thumbnail call fails. | We downsample to ~192×54 cells; a 1024-wide JPEG is 5+ source pixels per cell, plenty. A weak CPU decodes it in ~20 ms versus hundreds of ms for a 12 MP original, and it's a fraction of the bandwidth. Dropbox also converts WebP for free. |
| Local cache | `cache\` beside the exe, keyed by Dropbox `content_hash`, capped at `cache.maxFiles` (200), oldest-accessed evicted. | Repeats don't re-download. If the network or API is down, the loop keeps going over cached files instead of going black. |
| Auth | `SavviedMatrix.exe --auth` once, on this PC: opens the browser, you click Allow, paste the code into a small dialog, it writes `token.json` beside the exe. | Refresh tokens aren't device-bound, so `token.json` is copied to the TV PC with the exe. No browser needed on the kiosk. Re-run `--auth` if Dropbox ever revokes it. |
| Dev override | `--folder <absolute path>` reads a local directory instead of Dropbox. `--size WxH` runs in a borderless window of that size instead of fullscreen. | Iterate on rendering without the API; preview the exact 1920×1080 TV grid on this 1920×1200 screen. |
| Lifetime | **Continuous loop.** Each image holds for `displaySeconds` (10), then the next. Esc, any key, or click exits. | Runs for hours on the TV. |
| Ordering | Shuffle-bag: every image once in random order, then re-list the folder and reshuffle. | No immediate repeats; new uploads appear at the next cycle without a restart. |
| Display | Borderless fullscreen on the primary monitor, black background, cursor hidden, TopMost, display-sleep suppressed | Covers the taskbar; keeps the TV from blanking on the power-plan timeout. |
| Cell grid | Fixed 1:2 cell aspect. `columns` (default 192) → `cellW = 1920/192 = 10 px`, `cellH = 20 px`, 54 rows at 1080p. Font em size derived to fit the cell. | One fixed cell size lets Consolas (ramp glyphs) and MS Gothic (katakana rain glyphs) share a grid. ~10k cells, readable from across a room. |
| Rendering | **Glyph atlas + pixel compositor.** Every glyph × intensity bucket drawn once at startup. Frames are composed by copying atlas cells into an `int[]` backbuffer wrapped in a pinned `Bitmap`. No `DrawString` after startup. | One 10k-cell compose is ~8 MB of memcpy (~5 ms on a weak CPU). Makes static mode nearly free and rain feasible on this hardware. |
| Static mode (default) | Compose once per image. Next image is fetched, decoded and mapped on a background thread during the hold; the timer tick composes and swaps. | Zero work between swaps, no hitch at the swap, download latency hidden. |
| Rain mode (opt-in) | `rain.enabled: true` or `--rain`. Per image: **rain in** (`rain.inSeconds`, 3) → **hold** (`displaySeconds`) → **rain out** (`rain.outSeconds`, 2) → black → next rains in. Only changed cells recomposed per frame. | Per your request. Off by default; flip it on and read the logged frame time on the TV PC. |
| Rain timing | Time-based, not frame-based. Each column's stream is fully determined by `StreamParams` (delay, speed, trail) fixed at the start of the phase. | A slow machine drops frames and the rain looks choppier, but it always finishes on schedule. And because the stream is deterministic, the audio can be scheduled from the same parameters. |
| Audio engine | **Fully synthesized. No samples, no WAV files, nothing pre-recorded.** Every output value is computed at runtime: phase-accumulator oscillators (sine / triangle / square / saw), per-sample exponential envelopes, pitch glide, a one-pole filter, tanh soft-clip on the master. Our `Synth` class fills the float buffers; NAudio (`WaveOutEvent`, 44.1 kHz mono) is only the transport that hands those buffers to the sound card. Up to `sound.polyphony` (8) voices mixed inline. | Only real dependency in the project (~0.5 MB managed, single-file friendly), and it's replaceable by raw `waveOut` P/Invoke if we ever want zero deps. `System.Media.SoundPlayer` can't overlap sounds and only plays files. Synthesizing 44.1 kHz × 8 voices is a few million flops per second: nothing, and it runs on NAudio's own thread, never the UI thread. |
| Plink trigger | A **hit** is the moment a stream's head reaches the bottom row of the screen. Columns are grouped into `sound.bands` (16) vertical bands; each band plinks once per rain phase, when its center column hits. | 192 plinks in 3 s is noise; 16 is an arpeggio. Bands are the unit for both the trigger and the colour analysis. `sound.bands` = `columns` gives one plink per column if you want chaos. |
| Sound scheduling | Plink times are computed from `StreamParams` when the phase starts and queued to the synth at sample-accurate positions, compensated for output latency. Not triggered from frame ticks. | "Conditioned the same way the rain is": the same deterministic parameters drive both. Audio stays in sync with the visual hit even if the UI drops frames. |
| Pitch | **Hue → note (ROYGBIV → 7 scale degrees), luminance → octave.** Band's mean colour over the image region under it. Desaturated bands play the root; letterbox / black bands are rests. | Your suggestion. Left-to-right colour of the image becomes a melody; every image has its own tune. |
| Timbre / envelope | Saturation → waveform (sine → triangle → square → saw), stream speed → attack, trail length → decay, luminance → gain; a short downward pitch glide gives the "plink". Rain-out plays the same notes an octave lower, shorter, with a longer glide down. | "Tone, length and shape from characteristics of what's coming in." Each parameter maps to one audible dimension so it's legible. |
| Static-mode sound | On each swap, the band notes strum once over ~0.5 s. | Something still reacts to the image when rain is off. `sound.staticStrum` turns it off. |
| Packaging | `dotnet publish` self-contained single-file `win-x64`, ReadyToRun | TV PC probably lacks the .NET 10 runtime. ReadyToRun cuts JIT startup. ~70 MB exe. |
| Config | `config.json` beside the exe; CLI overrides for the things you'd tweak on the kiosk | Portable folder: exe + config + token + cache. |

## What I need from you

The build is not blocked on this; only the end-to-end test is.

1. **Create the Dropbox app** (2 minutes, needs your Dropbox login, which I can't do):
   - https://www.dropbox.com/developers/apps/create
   - Choose **Scoped access** → **App folder** → name it (e.g. `SavviedMatrix`; the name
     becomes the folder `Dropbox/Apps/SavviedMatrix/`).
   - **Permissions** tab: tick `files.metadata.read` and `files.content.read` → Submit.
   - **Settings** tab: copy the **App key**. Leave the secret alone; PKCE doesn't use it.
   - Paste the App key here or straight into `config.json` when it exists. The key is a
     public identifier (it appears in the browser URL during login), not a secret.
2. After the first build, run `SavviedMatrix.exe --auth` on this PC once and click Allow.
3. Drop test images into `C:\Users\josha\SVI Dropbox\Josh Attoun\Apps\SavviedMatrix\`
   (Dropbox desktop creates it after auth). They sync up; the exe pulls them down via API,
   exactly as the TV PC will.

If you'd rather point at an existing team folder instead of an app folder, say so: that needs
**Full Dropbox** access, a broader token, and the `Dropbox-API-Path-Root` header for team
space. I'd recommend against it for a kiosk.

## Sound design

**Analysis (per image, on the background thread, from the same small resized bitmap the
ASCII pass uses)**
- The screen's `columns` are split into `sound.bands` equal vertical bands. For each band,
  average the RGB of the image pixels under it (only the fitted region; a band with < 10%
  image coverage is a **rest**). Convert to HSL → `(hue, sat, lum, coverage)` = `BandStats`.

**Note mapping (`NoteMapper`, pure)**
- Hue bucket → scale degree. ROYGBIV boundaries (degrees): red 345–15, orange 15–45,
  yellow 45–70, green 70–170, blue 170–255, indigo 255–285, violet 285–345.
  - `scale: "major"` → C D E F G A B
  - `scale: "pentatonic"` → C D E G A C' D' (fewer clashing intervals; try both by ear)
- `sat < 0.15` → grey: play the root (C).
- Octave from luminance: `lum < 0.3` → octave 3, `< 0.7` → octave 4, else octave 5.
  Rain-out: one octave lower.
- `freq = 440 · 2^((midi − 69)/12)`.
- Waveform from saturation: `< 0.25` sine, `< 0.5` triangle, `< 0.75` square, else saw.
- Attack from stream speed (fast → 2 ms, slow → 15 ms). Decay from trail length
  (6 → 90 ms, 14 → 450 ms), exponential. Gain from luminance (0.25 → 1.0 range) × master.
- Plink glide: start 1 semitone above the target and glide down over 25 ms (rain-in);
  start at target and glide down 3 semitones over the whole decay (rain-out).
- One-pole low-pass at 6 kHz on saw/square to take the edge off the aliasing without
  losing the cheese.

**Scheduling (`PlinkScheduler`, pure)**
- Given `StreamParams[]` for the phase and `BandStats[]`: for each band, take its center
  column `c`, `t_hit = delay_c + (rows − 1) / speed_c`. Emit `(t_hit, Note)` unless the
  band is a rest. Rain-in and rain-out each produce their own list.
- Static swap: emit band notes at `i · 0.5 / bands` seconds (a strum).
- Output is sorted by time; the UI hands the whole list to the synth when the phase starts.

**Synth (`Synth`, implements NAudio's pull interface `ISampleProvider`: NAudio calls
`Read(float[] buffer)` and we compute every value in it)**
- Holds `long samplesRendered` (its clock), a min-heap of `(startSample, Note)`, and a
  fixed pool of `polyphony` voices. On `Read`, it advances in chunks: starts voices whose
  `startSample` falls inside the buffer at the exact offset (steals the oldest voice if the
  pool is full), renders each active voice, sums them, soft-clips with `tanh`, applies
  master volume.
- **Per-voice DSP, per output value** (`Voice`):
  - Oscillator: phase accumulator `phase += freq / sampleRate`, wrapped to `[0, 1)`.
    `sine = sin(2π·phase)`, `triangle = 4·|phase − 0.5| − 1`,
    `square = phase < 0.5 ? 1 : −1`, `saw = 2·phase − 1`. Naive (aliasing) on purpose:
    that *is* the cheesy mono-synth sound. PolyBLEP is a later option if it's too harsh.
  - Envelope: linear attack ramp over `attackMs`, then exponential decay
    `env *= exp(−1 / (decaySec · sampleRate))` each value; the voice frees itself when
    `env < 0.001`.
  - Glide: `freq = target · 2^(semis / 12)` with `semis` decaying from `glideSemitones`
    to 0 over `glideMs`.
  - Filter: one-pole low-pass `y += (x − y) · α`, `α = 1 − exp(−2π · cutoff / sampleRate)`,
    applied to square and saw only.
  - Output = `osc(phase) · env · gain`.
- Nothing is looked up from a table or a file; a voice is ~15 arithmetic ops per value.
- Scheduling from the UI: `startSample = clockAtPhaseStart + t_hit · sampleRate − latencySamples`
  where `clockAtPhaseStart` is read from the synth at the same instant the phase Stopwatch
  starts, and `latencySamples` = `WaveOutEvent.DesiredLatency` (60 ms). Events already in
  the past are played immediately.
- `AudioOutput` wraps `WaveOutEvent`. No audio device or init failure → log once and swap in
  a no-op; visuals unaffected. `sound.enabled: false` → nothing is initialised.

**Cost on the weak PC**
- Render: 44 100 samples/s × ≤ 8 voices × ~20 flops ≈ 7 Mflop/s on NAudio's thread.
- Memory: one 60 ms float buffer set, the voice pool. Negligible.
- Analysis: one pass over a ≤ 192×54 bitmap per image, already in hand.

## Dropbox API design

**Auth (`--auth`)**
1. Generate PKCE `code_verifier` (43–128 chars) and `code_challenge` (S256).
2. Open `https://www.dropbox.com/oauth2/authorize?client_id=<appKey>&response_type=code`
   `&code_challenge=<c>&code_challenge_method=S256&token_access_type=offline` in the default
   browser. No `redirect_uri`, so Dropbox shows the code on a page for copy-paste.
3. Small WinForms dialog: "Click Allow in the browser, then paste the code here." → OK.
4. POST `https://api.dropboxapi.com/oauth2/token` with `code`, `grant_type=authorization_code`,
   `code_verifier`, `client_id`. Save `refresh_token`, `account_id`, `uid` to `token.json`.
5. Show "Saved. You can close this." and exit 0.

**Runtime**
- Access token: POST `oauth2/token` with `grant_type=refresh_token`, `refresh_token`,
  `client_id`. Cached in memory with its `expires_in` (4 h); refreshed when < 5 min remain
  or on a 401 (once, then treat as auth failure).
- List: POST `https://api.dropboxapi.com/2/files/list_folder` `{"path": "<dropbox.folder>",
  "recursive": false}` where `""` is the app-folder root; follow `has_more` with
  `list_folder/continue`. Keep entries with `.tag == "file"` and an image extension
  (jpg/jpeg/png/gif/bmp/webp/tif/tiff). Each entry gives `path_lower`, `content_hash`, `size`.
- Fetch: POST `https://content.dropboxapi.com/2/files/get_thumbnail_v2` with header
  `Dropbox-API-Arg: {"resource":{".tag":"path","path":"<path_lower>"},"format":"jpeg",`
  `"size":"w1024h768","mode":"bestfit"}`. Save as `cache\<content_hash>.jpg`. If it fails
  (unsupported or > 20 MB) fall back to `files/download` and save the original. Skip the
  fetch entirely when the cache already has that hash.
- Errors: 429 / 5xx → honor `Retry-After` else exponential backoff to 60 s, keep serving the
  cache meanwhile. `invalid_grant` on refresh → log, show a dim "Dropbox auth expired, run
  --auth" line on the frame, keep serving the cache. Network unreachable → same, cache only.
- Listing happens once per playlist cycle, on the background thread. With 10+ s per image
  the API sees one small call every cycle and one thumbnail per image. Nowhere near limits.

**Cache**
- `cache\` beside the exe. File name = `content_hash` + extension. Last-access time is the
  file's `LastWriteTime` (touched on use). When count > `cache.maxFiles`, delete
  oldest-accessed until under the cap. Eviction runs after each cycle's listing.
- Startup with no network and a warm cache: loop over cached files immediately.

## Low-power PC considerations

**Startup (once)**
- Measure font, build the glyph atlas: ~50 glyphs × 17 intensity buckets × (10×20 px × 4 B)
  ≈ 700 KB. A few hundred `DrawString` calls, well under a second.
- Open the audio device (or fall back to silent).
- `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED)` so the TV never sleeps;
  cleared on exit.

**Per image (background thread, during the previous image's hold)**
- Thumbnail fetch (tens of KB to a few hundred KB) → decode a ≤1024×768 JPEG (~20 ms) →
  one `DrawImage` resize into `fitCols × fitRows` → luminance + contrast stretch → glyph and
  bucket per cell → band colour stats from the same small bitmap. Output is a `Frame`
  (`CellGrid` + `BandStats[]`). Source bitmap disposed right after the resize.

**Static mode, per swap (UI thread)**
- Full compose of 10k cells into the backbuffer: ~8 MB memcpy, ~5 ms. One 1080p blit.
  Queue the strum (16 events) to the synth.

**Rain mode, per frame (UI thread)**
- Only changed cells are recomposed. With every column streaming: head, trail (6–14 cells,
  flickering), newly revealed cell ≈ 2–3k cells ≈ 2 MB memcpy, plus one 1080p blit (~3–8 ms
  on an iGPU). Estimated 8–15 ms per frame against a 41 ms budget at 24 fps.
- Audio adds nothing to the UI thread: the plink list is queued once at phase start.
- If it stutters: `rain.fps` 15, or `columns` 128. The app writes measured average frame
  time to `SavviedMatrix.log` after each rain phase so you read the real number.

**Long-running hygiene**
- Backbuffer (8 MB), atlas, audio buffers and voice pool allocated once; `Frame`s are tiny;
  bitmaps disposed per image; cache capped on disk. Steady-state memory ≈ runtime + ~12 MB.
- ReadyToRun publish reduces cold start. Self-contained single-file extracts native libs to a
  temp dir on first run only.
- Resilience: a file that fails to fetch or decode is skipped and logged; the loop continues.
  Empty folder → dim "no images in Apps/<AppName>" line, re-check each interval. Audio device
  disappearing mid-run (TV turned off, HDMI audio gone) → NAudio raises `PlaybackStopped`;
  log, try to reopen once per cycle, otherwise stay silent. The app never exits on its own.

## Architecture

```
savvied_matrix/
  SavviedMatrix.sln
  src/SavviedMatrix/
    SavviedMatrix.csproj        net10.0-windows, UseWindowsForms, WinExe, PerMonitorV2, AllowUnsafeBlocks, PackageReference NAudio
    Program.cs                  parse args (--auth, --folder, --size, --rain, --mute, ...), load config, run
    AppConfig.cs                config.json model + CLI overrides
    Log.cs                      append-only SavviedMatrix.log beside the exe (skips, API errors, frame times)
    Source/
      IImageSource.cs           Task<IReadOnlyList<ImageRef>> ListAsync(); Task<string> FetchAsync(ImageRef) → local path
      ImageRef.cs               Id (path_lower or file path), ContentHash, Name
      LocalFolderSource.cs      dev: enumerate a directory; Fetch returns the path as-is
      Dropbox/
        DropboxAuth.cs          PKCE flow, token.json read/write, refresh-token → access-token
        DropboxClient.cs        list_folder(+continue), get_thumbnail_v2, download; backoff; JSON models
        DropboxSource.cs        IImageSource over DropboxClient + ImageCache
        ImageCache.cs           cache\ directory: lookup by hash, store, touch, evict(maxFiles)
        AuthDialog.cs           the paste-the-code dialog
    Playlist.cs                 pure: shuffle-bag over a list, refill callback, seeded RNG injectable
    Ascii/
      GridFit.cs                pure: (imgW, imgH, cols, rows) → (fitCols, fitRows, offsetX, offsetY)
      Luminance.cs              pure: Bitmap(cols×rows) → float[] in [0,1], contrast-stretched
      AsciiMapper.cs            pure: float[] → CellGrid (glyph index + bucket per cell)
      CellGrid.cs               byte[] Glyph, byte[] Bucket, int Cols, Rows
      Palette.cs                bucket → Color on the Matrix green scale; head color
      Frame.cs                  CellGrid + BandStats[]
      FrameBuilder.cs           ImageRef → Frame (fetch, decode, EXIF, fit, map, band stats); called off-thread
    Render/
      GlyphSet.cs               charset tables: ramp, katakana, rain stream glyphs
      GlyphAtlas.cs             startup: (glyph, bucket) → int[cellW*cellH] pixel block
      Compositor.cs             CellGrid (+ last-frame diff) → pinned backbuffer Bitmap
    Rain/
      StreamParams.cs           pure: per-column (delay, speed, trailLength) from (rows, seconds, rng)
      RainField.cs              pure: (params, t) → per-cell state: Hidden | Trail(k) | Head | Settled
      RainCompositor.cs         RainField state + target CellGrid → effective CellGrid for this frame
    Audio/
      BandStats.cs              pure: small bitmap + fit + bands → (hue, sat, lum, coverage)[]
      Note.cs                   freq, waveform, attackMs, decayMs, gain, glideSemitones, glideMs
      NoteMapper.cs             pure: BandStats + (speed, trail) + phase → Note; scale tables
      PlinkScheduler.cs         pure: StreamParams[] + BandStats[] + phase → (seconds, Note)[]
      Voice.cs                  oscillator + AD envelope + pitch glide + one-pole LP
      Synth.cs                  ISampleProvider: sample clock, event heap, voice pool, mix, master
      AudioOutput.cs            NAudio WaveOutEvent wrapper; latency; device failure → NullAudioOutput
    Ui/
      MatrixForm.cs             fullscreen/windowed form, phase state machine, timer, prefetch, input, keep-awake, audio hand-off
  specs/matrix/
    spec.md                     canonical requirements (MATRIX-*)
    tests/
      SavviedMatrix.Tests.csproj  xUnit, references src project
      PlaylistTests.cs
      GridFitTests.cs
      LuminanceTests.cs
      AsciiMapperTests.cs
      RainFieldTests.cs
      ImageCacheEvictionTests.cs
      BandStatsTests.cs
      NoteMapperTests.cs
      PlinkSchedulerTests.cs
  config.json                   copied to output beside the exe
  .gitignore                    + token.json, cache/
```

## Algorithm

**Startup**
1. Load config, resolve source (`--folder` → local; else Dropbox: load `token.json`, or
   exit with "run --auth first" if missing), keep-awake on, open audio.
2. Screen grid: `cellW = screenW / columns`, `cellH = 2·cellW`, `cols = screenW / cellW`,
   `rows = screenH / cellH`. Largest em size where the widest glyph's advance ≤ `cellW` and
   line height ≤ `cellH`, separately for Consolas and MS Gothic. With `--size WxH`, the
   "screen" is that window.
3. Build the glyph atlas: for each glyph in the union charset and each of 16 intensity
   buckets plus the "head" color, draw centered in a `cellW × cellH` bitmap on black with
   `TextRenderingHint.AntiAlias`, extract via `LockBits` into an `int[]`.
4. Allocate the backbuffer `int[screenW·screenH]`, pin it, wrap in
   `new Bitmap(w, h, stride, Format32bppPArgb, ptr)`.
5. Build the first `Frame` synchronously (show black meanwhile); start prefetching the
   second.

**Per image** (`FrameBuilder`, thread-pool thread)
1. **Pick.** `Playlist.Next()`; when the bag empties, `ListAsync()` again, reshuffle, evict
   cache.
2. **Fetch.** `FetchAsync(ref)` → local path (cache hit or thumbnail/download).
3. **Load.** `new Bitmap(path)`. Apply EXIF orientation (property 0x0112) via `RotateFlip`
   (relevant on the download fallback; verify in step 6 whether Dropbox thumbnails are
   already oriented, and skip if so). Failure → log, skip, pick again (max 10 tries, then
   the "no images" grid).
4. **Fit** (`GridFit`). Largest `(fitCols, fitRows)` inside `(cols, rows)` with
   `fitCols·cellW / (fitRows·cellH) == imgW / imgH`; centered offsets.
5. **Downsample.** One `DrawImage` into a `fitCols × fitRows` bitmap, bilinear. Dispose source.
6. **Luminance.** `L = 0.2126R + 0.7152G + 0.0722B` per cell, /255. Contrast stretch: 2nd and
   98th percentile → 0 and 1, clamp. Flat image (p2 == p98) → 0.5. Optional `gamma`.
7. **Map** (`AsciiMapper`).
   - `ramp` mode: glyph = `ramp[round(L · (ramp.Length − 1))]`, ramp `" .:-=+*#%@"`.
   - `katakana` mode: glyph = random from the katakana+digit set; `L < 0.08` → blank.
   - Bucket = `floor(L · 15)` (0–15). Cells outside the fitted region: blank, bucket 0.
   - Top 2% brightest cells get bucket 16 (the pale head color `#B4FFC8`).
8. **Band stats** from the same small bitmap (see Sound design).
9. Return the `Frame`.

**Palette.** Background `#000000`. Bucket 0 → `#002A00`, bucket 8 → `#008F11`,
bucket 15 → `#00FF41`, bucket 16 → `#B4FFC8`.

**Compose** (`Compositor`). For each cell whose `(glyph, bucket)` differs from the last
frame, copy the atlas block's rows into the backbuffer (one `Span<int>.CopyTo` per pixel
row). Track the dirty rectangle; `Invalidate(dirty)`. `OnPaint` does
`DrawImageUnscaledAndClipped(backbuffer)`. First frame after a swap is a full compose.

**Static mode loop** (`MatrixForm`)
- Timer at `displaySeconds · 1000`. Tick: await prefetch (normally already done), compose
  the new grid, swap, `Invalidate`, queue the strum, start the next prefetch.

**Rain mode loop** (`MatrixForm`)
- Phase machine: `RainIn → Hold → RainOut → RainIn(next)`. A `Stopwatch` gives phase time
  `t`. Timer at `1000 / rain.fps`; each tick evaluates the phase at the current `t`.
- **Phase start**: generate `StreamParams[]`, read the synth clock, run `PlinkScheduler`,
  queue the resulting events. All in one go before the first frame of the phase.
- **StreamParams** (per column, per rain phase): `delay ∈ [0, 0.35·seconds]`,
  `trail ∈ [6, 14]`, `speed = (rows + trail) / (seconds − delay) · jitter(0.85–1.15)` rows/s.
  Every column finishes at about `seconds`; jitter keeps them from finishing in lockstep.
- **RainField** at time `t`, column `c`: `head = (t − delay) · speed`. Row `r` is `Hidden`
  if `r > head`, `Head` if `r == floor(head)`, `Trail(k)` for `head − trail < r < head` with
  `k` = distance from head, `Settled` if `r ≤ head − trail`.
- **RainIn**: `Settled` → target grid; `Head` → random stream glyph at bucket 16; `Trail(k)`
  → random stream glyph at bucket `15 − k·15/trail`, re-rolled with 10% probability per
  frame (the mutating-glyph look); `Hidden` → black. Ends when every column is settled. Rain
  covers the whole screen; the image emerges in the fitted region, the letterbox settles to
  black. Plinks land as each band's center column reaches the bottom row.
- **Hold**: compose the target grid once; no work for `displaySeconds`.
- **RainOut**: fresh params; `Settled` → black, `Hidden` → current image. Ends when all
  columns settle. Plinks an octave down. Screen is black; next image's `RainIn` begins on
  the next tick. The next `Frame` was prefetched during `Hold`.
- Frame-time telemetry: average compose+paint per rain phase → log line.

**Input / exit.** `KeyDown` (any key) and `MouseClick` close the form. On close: cancel the
prefetch, stop audio, clear keep-awake, free the pinned buffer.

## Configuration

`config.json` beside the exe:

```json
{
  "dropbox": {
    "appKey": "PASTE_APP_KEY_HERE",
    "folder": ""
  },
  "displaySeconds": 10,
  "columns": 192,
  "glyphMode": "ramp",
  "gamma": 1.0,
  "cache": { "maxFiles": 200 },
  "rain": {
    "enabled": false,
    "fps": 24,
    "inSeconds": 3,
    "outSeconds": 2
  },
  "sound": {
    "enabled": true,
    "volume": 0.5,
    "bands": 16,
    "polyphony": 8,
    "scale": "major",
    "staticStrum": true,
    "latencyMs": 60
  }
}
```

- `dropbox.folder`: `""` is the app-folder root; `"/tv"` would be a subfolder inside it.
- CLI: `--auth`, `--folder <path>`, `--size WxH`, `--seconds <n>`, `--columns <n>`,
  `--mode ramp|katakana`, `--rain` / `--no-rain`, `--fps <n>`, `--mute`, `--volume <0..1>`.
- `token.json` (written by `--auth`): `{ "refresh_token", "account_id", "obtained_at" }`.
  Read-only, app-folder scoped. Still: don't commit it; `.gitignore` covers it.

## Spec draft (`specs/matrix/spec.md`)

Prefix `MATRIX`. Home repo: this one.

| ID | Summary | Verification |
|---|---|---|
| MATRIX-AUTH | `--auth` runs OAuth 2 PKCE with `token_access_type=offline`, no redirect URI, and writes `token.json` beside the exe; missing token at normal start → clear message and exit 2 | manual |
| MATRIX-SOURCE-DROPBOX | List the configured folder (non-recursive) via `files/list_folder` with continuation; image extensions only; fetch via `get_thumbnail_v2 w1024h768` with `files/download` fallback | manual |
| MATRIX-CACHE | Fetched files stored by `content_hash`; a hash already cached is not re-fetched; count never exceeds `cache.maxFiles` after eviction; eviction removes oldest-accessed first | **test** (eviction selection) + manual (hit/miss) |
| MATRIX-OFFLINE | On API/network failure the loop continues over cached files; backoff honors `Retry-After`; auth failure shows a hint on the frame | manual |
| MATRIX-SOURCE-FOLDER | `--folder <path>` reads a local directory instead of Dropbox (dev) | manual |
| MATRIX-PLAYLIST | Shuffle-bag: every listed file returned exactly once per cycle in random order; exhausted → re-list and start a new cycle; new files appear next cycle | **test** |
| MATRIX-SKIP-BAD | A file that fails to fetch or decode is skipped without ending the loop; empty folder shows a "no images" frame and re-checks each interval | manual |
| MATRIX-EXIF-ORIENT | Downloaded originals honor EXIF orientation before sampling | manual |
| MATRIX-GRID-FIT | Fitted grid preserves image aspect at 1:2 cell aspect, never exceeds the screen grid, is centered | **test** |
| MATRIX-LUMA-STRETCH | Rec. 709 luminance, 2–98 percentile stretch, flat image → 0.5 | **test** |
| MATRIX-GLYPH-MAP | Ramp index = `round(L·(n−1))`; katakana blanks below 0.08; bucket = `floor(L·15)`; top 2% → head bucket | **test** |
| MATRIX-DISPLAY-LOOP | Fullscreen (or `--size` window), black bg, green glyphs; static mode advances every `displaySeconds` with no visible blank frame; Esc/key/click exit; runs indefinitely otherwise | manual |
| MATRIX-RAIN-PHASES | With rain enabled: rain-in (`inSeconds`) → hold (`displaySeconds`) → rain-out (`outSeconds`) → black → next; disabled by default | manual |
| MATRIX-RAIN-FIELD | Given stream params and time `t`, each cell is exactly one of Hidden/Head/Trail(k)/Settled per the head/trail formula; all columns Settled at `t = seconds + tail`; at `t = 0` no head is below row 0 | **test** |
| MATRIX-SOUND-BANDS | Screen columns split into `sound.bands` equal bands; each band's HSL is the mean of image pixels under it; coverage < 10% → rest | **test** |
| MATRIX-SOUND-NOTE | Hue bucket → scale degree per the ROYGBIV table and chosen scale; `sat < 0.15` → root; octave by luminance thresholds 0.3/0.7; rain-out one octave lower; waveform by saturation thresholds 0.25/0.5/0.75; attack from speed, decay from trail, gain from luminance, within the stated ranges | **test** |
| MATRIX-SOUND-SCHEDULE | One event per non-rest band per rain phase at `t = delay_c + (rows−1)/speed_c` for the band's center column `c`; static swap emits a strum at `i·0.5/bands`; output sorted by time | **test** |
| MATRIX-SOUND-OUTPUT | Events play at their scheduled time compensated by `sound.latencyMs`; `sound.enabled: false` or `--mute` initialises no audio; no device → silent, logged, visuals unaffected | manual |
| MATRIX-KEEP-AWAKE | Display sleep suppressed while running, restored on exit | manual |
| MATRIX-CONFIG | `config.json` beside exe; documented CLI overrides win | manual |

The nine `test` requirements are pure functions over lists and arrays — deterministic (RNG
injected and seeded), silent when wrong, cheap to assert. Everything touching the screen,
timer, filesystem, network, power state, audio device, or Dropbox is `manual` with the
procedure written in the spec. How the synth *sounds* is tuned by ear, not specified.
Frame rate is not a requirement; it's a tunable with a logged measurement.

## Testing on this PC

- This screen is 1920×1200 physical at 125% scaling. DPI-aware, the app sees 1920×1200
  fullscreen. For the exact TV layout, run `--size 1920x1080` (borderless window at 0,0).
- End-to-end path is identical to the kiosk: `--auth` here once, `token.json` lands beside
  the exe, images pulled via API from `Apps/SavviedMatrix/`. Dropbox desktop on this PC is
  just a convenient way to put images into that folder.
- Sound: a few deliberately coloured test images (solid red, a rainbow gradient, a grey
  photo, a mostly-black one) make the hue → note mapping obvious by ear. Sync check: watch
  a stream hit the bottom and listen; adjust `sound.latencyMs` if the plink trails or leads.
- Kill the network (disable Wi-Fi) mid-run to verify MATRIX-OFFLINE.
- Deployment to the TV PC = copy `dist\` (exe, `config.json`, `token.json`; `cache\` is
  optional but a warm cache means instant first frame).

## Build & publish

```powershell
dotnet build SavviedMatrix.sln
dotnet test specs/matrix/tests
dotnet publish src/SavviedMatrix -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

First run on the TV PC: SmartScreen will warn about an unsigned exe ("More info → Run
anyway"). Code signing is out of scope. Auto-start: shortcut in `shell:startup`.

## Implementation steps

1. Write `specs/matrix/spec.md` from the table above.
2. `dotnet new sln`, `dotnet new winforms` in `src/SavviedMatrix`, `dotnet new xunit` in
   `specs/matrix/tests`, add NAudio, wire references, `.gitignore` additions.
3. Pure core: `Playlist`, `GridFit`, `Luminance`, `AsciiMapper`, `Palette`, `StreamParams`,
   `RainField`, `ImageCache` eviction, `BandStats`, `NoteMapper`, `PlinkScheduler`. Nine
   xUnit test files red-first with `// Traces: MATRIX-*`.
4. `LocalFolderSource`, `AppConfig`, `Log`, `Program`, `FrameBuilder`. `GlyphAtlas`,
   `Compositor`, `MatrixForm` static mode. Run with `--folder` against a local directory
   and `--size 1920x1080`; tune `columns`, ramp, palette by eye.
5. `Voice`, `Synth`, `AudioOutput`. Static-mode strum first (simplest trigger); tune the
   plink by ear: glide amount, decay range, filter, waveform thresholds.
6. `DropboxAuth` + `AuthDialog` + `DropboxClient` + `ImageCache` + `DropboxSource`.
   Needs the App key. Run `--auth`, drop images in the app folder, run for real. Verify
   thumbnail orientation on a phone photo; check the offline behavior.
7. `RainCompositor` + phase machine + plink scheduling at phase start. Tune trail, speeds,
   flicker by eye; check audio/visual sync at the bottom row; read the logged frame time.
8. Soak: 30+ minutes in rain mode with sound; memory in Task Manager, frame time and API
   errors in the log, cache count stays capped, no audio glitches.
9. Publish. Copy `dist\` to the TV PC. Check cold start, swap smoothness, rain frame time,
   audio over HDMI, SmartScreen, TV stays awake. Decide rain on/off and `fps` from the
   measured number.
10. Merge into `spec.md`, move this plan to `specs/matrix/archive/`.

## Out of scope (for now)

- Rain "wipe" variant (one pass erases the old image and reveals the new, no black gap).
  Easy follow-up on the same `RainField`.
- Crossfade. Per-pixel blending every frame; the atlas approach doesn't help.
- Sound for individual trail glyph flickers or per-column hits at full density. The band
  trigger is the sane default; `sound.bands = columns` is the escape hatch.
- Reverb/delay on the synth. A cheap feedback delay would suit the aesthetic; add later if
  the dry plinks feel thin.
- HEIC. Dropbox thumbnails may or may not cover it; verify in step 6, otherwise skip those.
- Code signing, installer.
- Multi-monitor selection. Primary screen only.
- Subfolders. Non-recursive listing.
- Team-folder / Full Dropbox access (see "What I need from you").
