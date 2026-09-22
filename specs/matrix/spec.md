# MATRIX — Matrix ASCII image viewer

**Home repo:** savvied_matrix (this repo)
**Requirement prefix:** `MATRIX`
**Tests:** `specs/matrix/tests` (xUnit, `dotnet test`)

A Windows executable that runs unattended on a 1080p television driven by a low-power PC
with no Dropbox desktop client. It pulls images from a Dropbox app folder over the Dropbox
API and cycles through them as ASCII art on black, optionally handing over from one image
to the next in a single pass of Matrix digital rain, with synthesized plinks whose pitch
comes from the colours of the image.

---

## Source and authorization

### MATRIX-AUTH: One-time Dropbox authorization
**Applies to:** savvied_matrix
**Verification:** manual

`SavviedMatrix.exe --auth` shall run the OAuth 2 PKCE flow against Dropbox with
`token_access_type=offline` and no `redirect_uri`, and write the resulting refresh token to
`token.json` beside the executable. Starting the viewer without `token.json` and without
`--folder` shall show a message naming `--auth` and exit with code 2.

The app secret shall never be required, stored or transmitted.

**Verification (manual):**
1. Put the app key in `config.json` under `dropbox.appKey`.
2. Run `SavviedMatrix.exe --auth`. The default browser opens the Dropbox consent page.
3. Click Allow, copy the code, paste it into the dialog, click OK.
4. Confirm `token.json` appears beside the executable and contains a `refreshToken`.
5. Rename `token.json` aside and run the viewer with no arguments: it reports that it is
   not authorized and exits with code 2 (`echo %ERRORLEVEL%`).

### MATRIX-SOURCE-DROPBOX: Listing and fetching from Dropbox
**Applies to:** savvied_matrix
**Verification:** manual

The viewer shall list `dropbox.folder` non-recursively via `files/list_folder`, following
`has_more` with `list_folder/continue`, and keep only entries of type `file` with an image
extension. Images shall be fetched via `files/get_thumbnail_v2` at `w1024h768` in JPEG,
falling back to `files/download` when the thumbnail call fails.

A thumbnail is used rather than the original because the image is downsampled to roughly
192 by 54 cells; a 1024-pixel-wide source is several source pixels per cell, decodes in
about twenty milliseconds on a weak CPU, and costs a fraction of the bandwidth.

**Verification (manual):**
1. Put at least 3 images in the app folder, including one larger than 10 MP and one with a
   non-ASCII filename.
2. Run the viewer. All of them appear in turn.
3. Confirm in `SavviedMatrix.log` that no fetch errors were logged.
4. Confirm the files in `cache/` are far smaller than the originals, proving the thumbnail
   path was used.

### MATRIX-CACHE: Content-addressed local cache
**Applies to:** savvied_matrix
**Verification:** test (eviction selection) + manual (hit/miss)

Fetched images shall be stored in `cache/` beside the executable, named by the Dropbox
`content_hash`. An image whose hash is already cached shall not be re-fetched. After each
listing, the cache shall be evicted down to `cache.maxFiles` by deleting the
least-recently-used files first.

**Acceptance criteria (eviction selection):**
- A cache at or under the cap selects nothing for deletion.
- A cache over the cap selects exactly `count - maxFiles` files.
- The files selected are the ones with the oldest last-write time.
- `maxFiles` of 0 or less selects every file.
- Ties in timestamp are broken deterministically by ordinal path.

**Verification (manual, hit/miss):** run the viewer twice over the same folder; the second
run logs no fetches and the cache file count does not grow.

### MATRIX-OFFLINE: Keep running without the network
**Applies to:** savvied_matrix
**Verification:** manual

When the Dropbox API is unreachable or returns an error, the viewer shall keep cycling over
the images already in the cache rather than going black or exiting. HTTP 429 and 5xx shall
be retried honouring `Retry-After` where present, otherwise with exponential backoff.
An expired refresh token shall be logged and surfaced on screen while the cache keeps playing.

A wall display that blanks the moment the wifi drops is worse than one showing yesterday's
pictures.

**Verification (manual):**
1. Run the viewer until several images are cached.
2. Disable the network adapter.
3. Confirm the slideshow continues and `SavviedMatrix.log` records the failure and fallback.
4. Re-enable the network and confirm new images appear at the next cycle.

### MATRIX-SOURCE-FOLDER: Local folder override for development
**Applies to:** savvied_matrix
**Verification:** manual

`--folder <path>` shall read images from a local directory instead of Dropbox, bypassing
authorization and the cache entirely.

**Verification (manual):** `SavviedMatrix.exe --folder .\testimages --size 1280x720` shows
the test images without needing `token.json`.

---

## Playback

### MATRIX-PLAYLIST: Shuffle-bag ordering
**Applies to:** savvied_matrix
**Verification:** test

Images shall be served in a shuffled order in which every listed image appears exactly once
per cycle. When a cycle is exhausted, the folder shall be listed again and a new cycle
started, so images added while the app is running appear without a restart. The first image
of a cycle shall not be the last image of the previous cycle when more than one image exists.

Plain random selection would repeat one image within a minute while skipping another for an
hour, which on a wall display is immediately obvious.

**Acceptance criteria:**
- Draining one cycle of n images yields exactly those n images, each once.
- The order differs from the listing order.
- Refilling starts a new cycle and discards anything left in the bag.
- A newly added file appears in the following cycle.
- Across 40 cycles of 8 images, every image is served exactly 40 times.
- Across 300 different seeds, the last image of one cycle is never the first of the next.
- A one-image list repeats, having no alternative.
- Refilling with an empty list leaves the playlist empty and `TryNext` returns false.

### MATRIX-SKIP-BAD: A bad file does not end the show
**Applies to:** savvied_matrix
**Verification:** manual

An image that cannot be fetched or decoded shall be logged and skipped, and the loop shall
continue. Ten consecutive failures shall end the attempt for that slot, not the app. An
empty or entirely unreadable folder shall display a "NO IMAGES" message naming the source
and re-check each interval. The app shall never exit on its own.

**Verification (manual):**
1. Put a zero-byte file named `broken.jpg` and a renamed text file `notreally.png` in the
   folder alongside good images.
2. Run the viewer. The good images still cycle; `SavviedMatrix.log` records both skips.
3. Point `--folder` at an empty directory: the screen reads NO IMAGES and the app stays up.

### MATRIX-EXIF-ORIENT: Honour EXIF orientation
**Applies to:** savvied_matrix
**Verification:** manual

Images carrying an EXIF orientation tag shall be rotated accordingly before sampling.

**Verification (manual):** show a phone photograph taken in portrait orientation and confirm
it appears upright rather than on its side.

---

## Rendering

### MATRIX-GRID-FIT: Aspect-preserving placement
**Applies to:** savvied_matrix
**Verification:** test

An image shall occupy the largest centred region of the character grid that preserves its
aspect ratio, given that character cells are twice as tall as they are wide. The region
shall never exceed the screen grid. Cells outside it are letterbox and render black.

**Acceptance criteria:**
- A 16:9 image on a 192 by 54 grid with 10 by 20 pixel cells fills the grid exactly.
- A square image on that grid occupies 108 by 54 cells at column offset 42.
- A 1000 by 2000 portrait occupies 54 by 54 cells at column offset 69.
- A 4000 by 1000 panorama occupies 192 by 24 cells at row offset 15.
- For any image, the fitted region stays within the grid and the offsets are non-negative.
- Displayed aspect ratio matches the image's to within one cell of rounding.
- With square cells, a square image fits a square region.
- Zero or negative image dimensions are rejected.

### MATRIX-LUMA-STRETCH: Luminance and contrast stretch
**Applies to:** savvied_matrix
**Verification:** test

Per-cell brightness shall be Rec. 709 relative luminance. It shall then be stretched so the
2nd percentile maps to 0 and the 98th to 1, clamping the tails, optionally followed by gamma.
An image whose percentiles coincide shall map to a uniform 0.5 rather than dividing by zero.

A ten-step character ramp has so little dynamic range that an unstretched photograph
collapses into two or three glyphs and reads as mush.

**Acceptance criteria:**
- Black maps to 0 and white to 1.
- Pure red, green and blue map to 0.2126, 0.7152 and 0.0722.
- A flat image, and an all-black image, map to a uniform 0.5.
- A narrow input band is expanded to span 0 to 1.
- The mapping is monotonic.
- Extreme outliers are clamped without compressing the bulk of the values.
- Output always lies within 0 to 1, including with gamma applied.
- Gamma above 1 darkens the mid-tones while leaving the endpoints fixed.
- Empty input produces empty output.

### MATRIX-GLYPH-MAP: Characters
**Applies to:** savvied_matrix
**Verification:** test

Four character sets shall be selectable. In the three ramp modes a cell's character shall be
`ramp[round(L * (n - 1))]` over that mode's ramp: `ramp` is the ten-step `" .:-=+*#%@"`,
`dense` is the seventy-step standard ASCII-art ramp, and `blocks` is the five-step Unicode
shading run. In `katakana` mode the character shall be drawn at random from the half-width
katakana set, with cells below 0.08 left blank. Cells outside the fitted region shall be
blank and black.

The brightest two per cent of cells shall take the palette's highlight colour.

**Acceptance criteria:**
- Every ramp mode maps 0 to space and 1 to its densest glyph.
- The ten-step ramp rounds to the nearest step in between.
- The dense ramp yields more than 40 distinct glyphs over 64 evenly spaced values, where
  the short ramp yields exactly 10.
- Blocks mode emits only the five shading characters.
- Katakana blanks cells below 0.08 and draws at the threshold itself.
- Katakana mode emits only katakana glyphs or blanks.
- Cells outside the placement are blank with colour 0.
- With 96 dark and 4 bright cells, the bright ones take the highlight colour.
- An all-black image and a grid under 50 cells produce no highlight at all.
- A flat image produces no highlight either, rather than turning every cell into one.
- Mapping is deterministic for a given seed.

### MATRIX-PALETTE: Sixteen colours
**Applies to:** savvied_matrix
**Verification:** test

Every cell shall carry a colour index into a palette of sixteen slots plus one highlight.
Two palettes shall be selectable.

In `matrix` the sixteen are a green brightness ramp, the image's own hues are discarded, and
slot 0 is deliberately not pure black so that dark parts of the picture still read as lit
characters.

In `ansi` the sixteen are the standard ANSI terminal colours, and a cell is assigned one
deliberately rather than by nearest colour: below the chroma threshold it takes a neutral by
brightness, below the black threshold it is black, and otherwise it takes the dim or bright
variant of whichever of the six ANSI hues its own hue falls nearest. Brightness is already
carried by the choice of character, which frees the sixteen slots to carry hue.

Nearest-colour matching was tried first and is wrong for this. Four of the sixteen slots are
neutral, and a colour has to be around 0.4 saturated before it is closer to a hue than to a
grey, so an ordinary photograph came out looking monochrome. Colour conversion also runs
through HSL rather than by scaling RGB channels, because scaling and then clamping at 255
distorts saturation.

Sixteen is not an arbitrary limit. It is what an ANSI terminal has, and quantising to it is
what makes the output read as terminal art rather than as a photograph behind a filter.

Rain trails shall fade along an explicit path through the palette, because under `ansi` the
sixteen slots are hues rather than a ramp and walking them numerically would cycle the trail
through unrelated colours.

**Acceptance criteria:**
- Both palettes expose 16 slots plus a highlight at index 16.
- Matrix slots are green-dominant and non-decreasing in brightness; slot 0 is not black.
- Matrix brightness covers all 16 slots across the input range, and ignores hue: two
  different hues at the same brightness give the same slot.
- ANSI slots are the standard sixteen values, and an exact palette colour matches itself.
- ANSI distinguishes hues: mid-brightness red, green and blue give three different slots.
- Each of the six hue sectors yields its documented dim slot below the brightness split
  and its bright slot above it.
- A cell with almost no colour in it falls back to a neutral.
- A muted cell that reads as neutral at scale 1 takes a hue once the scale is raised.
- A cell below the black threshold is black whatever colour it started as.
- Both trails start at the highlight and end at slot 0, fade monotonically in brightness,
  and under `ansi` stay green or neutral throughout.
- Nearest-colour matching always returns a valid slot for any input.
- Index and fade lookups clamp out-of-range input.

### MATRIX-DISPLAY-LOOP: The display itself
**Applies to:** savvied_matrix
**Verification:** manual

The viewer shall run borderless and fullscreen on the primary monitor with a black
background and a hidden cursor, or in a borderless window when `--size WxH` is given. Each
image shall be held for `displaySeconds`, defaulting to 10. Any key press or mouse click
shall exit. The next image shall be fetched and converted on a background thread so the
swap shows no blank frame; when the next image is not ready in time, the current one stays
up longer rather than the screen going black.

**Verification (manual):**
1. Run with no arguments on the display PC. The picture covers the taskbar; no cursor.
2. Time three transitions against a watch: each is about 10 seconds.
3. Watch a transition closely: there is no black flash or stutter.
4. Press a key, then run again and click the mouse. Both exit immediately.

### MATRIX-KEEP-AWAKE: Do not let the screen sleep
**Applies to:** savvied_matrix
**Verification:** manual

While running, the viewer shall suppress display sleep, and shall restore the normal power
policy on exit.

**Verification (manual):** set the power plan to turn the display off after 1 minute, run
the viewer for 3 minutes and confirm the screen stays on. Exit, wait a minute, and confirm
the screen then turns off as configured.

### MATRIX-TONE: Making the picture readable at thirty levels
**Applies to:** savvied_matrix
**Verification:** test

Brightness shall pass through three stages before it selects a character: the global
contrast stretch, optional local contrast, and tone equalisation. Saturation shall be
normalised per image by a single scale aimed at the median of its mid-tone pixels, never
below 1, and disabled for images with no real colour anywhere.

The reason is the level budget. There are only about thirty distinguishable characters, so
how the image's tones are *distributed* across them matters more than the range they span.
A photograph that is two thirds dark background spends two thirds of its cells on the bottom
few characters and the subject gets whatever is left, which is the difference between a
picture and a silhouette. Measured over a set of photographs, equalisation at 0.8 raised the
glyph-level entropy from 3.10 bits per cell to 4.39 and cut neutral-coloured cells from
6.8 per cent to 3.1.

Local contrast is off by default, and that is a measured decision rather than an oversight:
every setting above zero carried less detail than zero did, because it clamps cells onto the
first and last character faster than it reveals structure. It is retained as a lever for
flat, evenly lit sources.

**Acceptance criteria:**
- Equalisation raises the entropy of a piled-up distribution by more than one bit.
- Equalisation is monotonic, stays in range, and handles empty and flat input.
- Strength 0 is the identity; a partial strength lands between the input and full.
- Local contrast increases the separation between a region and its surroundings.
- Local contrast of 0 returns the input unchanged, and any amount stays in range.
- Blurring a uniform field changes nothing.
- A muted image scales up, a vivid one is left at 1, a monochrome one is never colourised.
- The scale aims the median of the samples at the target.
- Only mid-tone pixels are sampled, since saturation is meaningless at the extremes.
- The scale never falls below 1 or exceeds the cap, for any input.
- HSL round-trips to the same colour, and raising lightness through it holds saturation
  where scaling RGB channels does not.

### MATRIX-HALFBLOCK: Twice the vertical resolution
**Applies to:** savvied_matrix
**Verification:** test

In `half` mode a cell shall draw no character. Instead it shall be split horizontally, the
top half taking the colour of one image sample and the bottom half the colour of the sample
below it, which doubles the vertical resolution and, at the fixed 1:2 cell shape, makes the
effective pixels square. The image fit shall therefore be computed in sample space with
square cells. Cells outside the image, and every cell drawn by the rain, shall remain
ordinary character cells.

This is the most detail the grid can carry and the point at which the picture stops being
made of text, so it is a mode rather than the default. Brightness is no longer carried by a
character, which makes the sixteen-colour quantisation the only thing describing the image
and is why dithering matters more here than anywhere else.

**Acceptance criteria:**
- A cell's two halves take the colours of two vertically adjacent samples.
- Half-block cells are marked as such and ordinary character cells are not.
- Cells outside the placement stay ordinary character cells.
- A newly created grid is entirely ordinary character cells.
- The mode is recognised as `half`, `halfblock` and `half-block`, case-insensitively.

### MATRIX-DITHER: Breaking up the banding
**Applies to:** savvied_matrix
**Verification:** test

Colour thresholds shall be perturbed by an ordered Bayer pattern whose offset depends only
on a cell's coordinates. The pattern shall average to exactly zero and shall be disabled by
a strength of zero.

Sixteen colours with hard thresholds turn a gradient into flat bands with visible edges.
Nudging each cell before the threshold turns the edge into a stipple that the eye reads as
an intermediate colour. The pattern is ordered rather than error-diffused so that it is
stable frame to frame: error diffusion would crawl as the rain swept across the picture. It
must average to zero, or every image comes out a fraction darker than it should.

**Acceptance criteria:**
- Strength zero gives an offset of exactly zero everywhere.
- Offsets stay within plus or minus half the strength.
- The matrix averages to zero to five decimal places.
- Every cell of the tile gets a distinct offset.
- The pattern repeats on its tile and handles negative coordinates.
- A flat patch just below a threshold renders as one colour undithered and two dithered.

### MATRIX-CALIBRATE: Ramps measured from the font
**Applies to:** savvied_matrix
**Verification:** test

The ASCII ramps shall be rebuilt at startup from the ink coverage the atlas measures for
each glyph at the cell size actually in use, so that a step up in brightness is a step up in
ink. Glyph tinting shall be normalised against the densest available glyph, so that the
picture does not dim as the cell size shrinks. Where two glyphs are equally dark the one whose ink is nearer the middle of the cell
shall be preferred. The darkest step shall always be the empty cell.

A conventional ramp is ordered by eye in somebody else's font. At ten pixels in Consolas
several of its steps are indistinguishable while others jump, which shows up as banding.
The centring rule matters because an underscore and a quotation mark have almost the same
coverage but tile into visible lines along the bottom or the top of the picture.

**Acceptance criteria:**
- The calibrated ramp is non-decreasing in measured coverage.
- It starts at the blank cell and ends at the darkest available glyph.
- Given two equally dark candidates, the centred one is chosen.
- Calibration is ignored, leaving the nominal ramps, when the measurements do not cover
  every glyph.

---

## Rain

### MATRIX-RAIN-TRANSITION: One pass hands over from image to image
**Applies to:** savvied_matrix
**Verification:** manual

When rain is enabled, images shall hand over to one another in a single rain pass lasting
`rain.seconds`: ahead of each falling head the outgoing image is still standing, the head
and its trail are the rain, and behind the trail the incoming image has settled. The screen
shall never be blank between images. The first image shall arrive by the same pass, with
black standing in for the outgoing image.

Raining the old image off and the new one on in one motion reads as a single continuous
event. Fading to black and starting again reads as two, with a dead gap in the middle.

The animation is the one part of the app that costs work every frame, so it is opt-in and
the measured cost is logged rather than assumed.

**Verification (manual):**
1. Run with `--rain --seconds 4`. Confirm each handover is one continuous pass and that the
   screen is never fully black between two images.
2. Confirm `SavviedMatrix.log` reports the frames and the average compose-and-paint time
   for each transition, against the frame budget.
3. If the average approaches the budget on the display PC, reduce `rain.fps` to 15 or
   `columns` to 128 and re-measure.

### MATRIX-RAIN-FIELD: The rain model
**Applies to:** savvied_matrix
**Verification:** test

Each column shall have a stream with a fixed delay, speed and trail length chosen when the
phase begins, such that every column lands at approximately the phase duration. At any time
`t`, a cell shall be in exactly one of four states derived from the head position
`(t - delay) * speed`: hidden ahead of the head, head at its row, trail within the trail
length behind it, settled beyond that.

Evaluating the field from time rather than from a frame counter is what lets a slow machine
drop frames without the rain slowing down or stalling, and it is what lets the audio be
scheduled before the phase starts.

**Acceptance criteria:**
- Before the delay elapses, every row of that column is hidden.
- With no delay at t=0, row 0 is the head and row 1 is hidden.
- At 10 rows per second and t=2.05, row 20 is the head and row 21 is hidden.
- Rows 19 down to 16 are trail with distances 1 to 4 for a trail length of 5.
- Rows beyond the trail have settled.
- Every cell is in a defined state, and only trail cells carry a non-zero distance.
- The bulk evaluation agrees with the single-cell function at every cell.
- Every column has settled at the computed settle time, and not 0.05s before it.
- Generated streams respect the documented delay, trail and jitter bounds.
- Every column settles within 0.7 and 1.5 times the requested duration.
- Generation is deterministic for a given seed.

---

## Sound

All pitches, waveforms and envelopes are computed sample by sample from oscillator maths.
The application contains no audio samples, wavetables or sound files, by deliberate choice.

### MATRIX-SOUND-BANDS: Colour analysis per band
**Applies to:** savvied_matrix
**Verification:** test

The screen's columns shall be divided into `sound.bands` equal vertical bands. Each band's
colour shall be the mean of the image pixels beneath it, converted to hue, saturation and
luminance. A band covered less than ten per cent by the image shall be a rest.

**Acceptance criteria:**
- Band column ranges partition the screen without gaps or overlap.
- A band fully over the image reports coverage 1; one fully in letterbox reports 0.
- A solid-colour region yields that colour's hue and saturation.
- A greyscale region reports saturation below the greyscale threshold.

### MATRIX-SOUND-NOTE: Colour to note
**Applies to:** savvied_matrix
**Verification:** test

Hue shall select a scale degree on the ROYGBIV boundaries: red 345 to 15, orange 15 to 45,
yellow 45 to 70, green 70 to 170, blue 170 to 255, indigo 255 to 285, violet 285 to 345.
The degree shall index the configured scale, major `{0,2,4,5,7,9,11}` or pentatonic
`{0,2,4,7,9,12,14}`. A desaturated band shall play the root. Luminance shall select the
octave at thresholds 0.3 and 0.7. Saturation shall select the waveform at thresholds 0.25,
0.5 and 0.75. Stream speed shall set the attack, trail length the decay, and luminance the
gain, each within its documented range.

The mapper also supports a rain-out phase an octave below, shorter and quieter, with the
glide falling away instead of rising into the note. Nothing calls for it since
MATRIX-RAIN-TRANSITION replaced the two-pass handover with one, and the plinks for a
transition come from the incoming image. It is retained, and tested, against wanting an
outgoing counter-melody later.

**Acceptance criteria:**
- Each of the seven hue buckets yields its expected semitone, including the red wrap
  across 345 to 360 to 15.
- Saturation below 0.15 forces the root regardless of hue.
- The three luminance bands select octaves 3, 4 and 5.
- The four saturation bands select sine, triangle, square and saw.
- Attack is 15 ms at 5 rows per second and 2 ms at 60, clamped beyond.
- Decay is 90 ms at trail 6 and 450 ms at trail 14, clamped beyond.
- Rain-out notes are exactly one octave below the equivalent rain-in note.

### MATRIX-SOUND-SCHEDULE: When the plinks fire
**Applies to:** savvied_matrix
**Verification:** test

A plink shall fire when a band's centre column reaches the bottom row, at the time given by
that column's stream parameters. Rest bands shall produce no note. A static-mode swap shall
strum the band notes evenly over the strum duration. Output shall be sorted ascending by time.

Scheduling from the stream parameters rather than from frame ticks is what keeps the sound
aligned with the picture when the machine drops frames.

**Acceptance criteria:**
- One event per non-rest band; rests excluded.
- Fire time equals the band centre column's time at the bottom row.
- Output is sorted ascending by time.
- Empty input yields an empty array rather than null.
- The static strum spaces notes evenly and leaves a rest's slot as a gap.

### MATRIX-SOUND-OUTPUT: Playback
**Applies to:** savvied_matrix
**Verification:** test (signal path) + manual (device and sync)

Scheduled notes shall play at their scheduled time, compensated by the output latency so
the sound lands with the visual event. `sound.enabled: false` or `--mute` shall initialise
no audio device at all. When no audio device is available, the app shall log it once and
continue silently with the visuals unaffected.

The signal path shall not produce discontinuities audible as clicks or crackle. Saw and
square oscillators shall be band limited. A voice cut short, whether stolen by a new note
or silenced by a panic, shall ramp to zero rather than stop mid-waveform. A note shall end
on exactly zero. Voices shall be summed with enough attenuation that the soft clipper is a
safety net for pathological clusters rather than the thing doing the mixing.

Every one of those is a way to put a step discontinuity into the output, and a step
discontinuity is exactly what a listener reports as crackle.

**Acceptance criteria (signal path):**
- Band-limited saw and square leave the folded alias below a tenth of the naive
  oscillator's, while keeping the fundamental at 2/pi and 4/pi to two decimal places.
- The largest sample-to-sample step across a stolen voice stays below 0.05.
- A note that has finished ends on exactly zero.
- A realistic cluster peaks at or below 1.0 before the soft clipper.
- The mix gain is the reciprocal square root of the polyphony.
- Notes start on the exact scheduled sample.
- A panic silences the output within the declick ramp.

**Verification (manual):**
1. Run with `--rain` and sound on. Watch a stream reach the bottom of the screen and confirm
   the plink lands with it, not noticeably before or after. Adjust `sound.latencyMs` if it
   leads or trails.
2. Show a strongly coloured test image and a greyscale one; confirm the first plays a spread
   of pitches and the second plays one repeated note.
3. Run with `--mute` and confirm silence and no audio entry in the log.
4. Disable the audio device in Windows, run again, and confirm the visuals are unaffected
   and the log records the fallback.

---

## Configuration

### MATRIX-CONFIG: Configuration and overrides
**Applies to:** savvied_matrix
**Verification:** manual

Settings shall be read from `config.json` beside the executable, with documented command
line options overriding them. A missing or unparseable config shall fall back to defaults
with a logged warning rather than failing to start. An unrecognised option shall produce a
message and exit code 1.

**Verification (manual):**
1. Set `displaySeconds` to 3 in `config.json`, run, and confirm the faster cycle.
2. Run with `--seconds 12` and confirm the option wins over the file.
3. Corrupt `config.json`, run, and confirm it starts on defaults with a logged warning.
4. Run with `--nonsense` and confirm the message and exit code 1.

---

## Out of scope

Recorded in the archived plan and deliberately not implemented: crossfades, reverb or delay
on the synth, HEIC support, code signing, an installer, multi-monitor selection, subfolder
recursion, and full-Dropbox (non-app-folder) access. The rain wipe, listed there as out of
scope, is now the shipping behaviour under MATRIX-RAIN-TRANSITION.
