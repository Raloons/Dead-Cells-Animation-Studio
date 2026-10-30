# Architecture — Dead Cells Animation Studio (AS-M1)

## Goals

- Decode and play **genuine** Dead Cells animations outside the game.
- Standalone tool: no dependency on any mod runtime, no game modification.
- Never bundle or redistribute proprietary game assets.
- Modular decoders so new game versions / formats can be supported later.

## Native asset findings (summary)

Dead Cells ships all textures in `res.pak` (PAK v1 container). Hero graphics
live in `atlas/beheaded.atlas` (binary **BATL**) plus page PNGs
(`beheaded0/1/2.png`; `_n` normal maps exist for lighting and are ignored).
Each animation is a run of regions named `AnimName_NN`; each region is one
pre-rendered full-body frame cropped from a page and pasted top-left onto a
fixed-size canvas. `atlas/beheaded_tracks.json` carries per-frame bone
positions (reserved for future hitbox/weapon overlays, not needed for
frame playback). Gameplay timing (`animId`, `hitFrame`, `charge`,
`animSpd`, `fxId`) lives in CDB weapon rows, not in the atlas.

Full methodology, byte layouts and validation: `docs/AS-M1-REPORT.md`.

## Solution map

```text
DeadCells.AnimationStudio.slnx
├── src/DcAnim.Core  (net10.0, UI-free)
│   ├── Pak/          PakReader, PakEntry
│   ├── Atlas/        BatlParser, BatlAtlasData/BatlRegion, AtlasFrameComposer
│   ├── Model/        AnimationClip, ClipFrame, AnimationTiming
│   ├── Catalog/     AnimationCatalog, AnimationDescriptor
│   ├── Playback/     PlaybackEngine
│   └── Sources/      AnimationLoader, GameSourceLocator
├── src/DcAnim.App   (net10.0-windows, Avalonia 11)
│   ├── MainWindow    asset browser / preview / inspector / timeline wiring
│   ├── Controls/     PreviewControl, TimelineControl
│   ├── Services/     FrameBitmap (RGBA → WriteableBitmap)
│   └── data/         timing-overrides.json (hand-verified CDB linkage overlay)
└── tests/DcAnim.Tests (net10.0, xUnit v3)
```

## Component contracts

### PakReader (`Core/Pak`)

- Parses PAK v1: `PAK\x01`, data-section offset, hash string, version, root count,
  depth-first entries (`u8 len + name + u8 type`; file = `u32 offset/size/adler32`,
  dir = `u32 childCount`). Offsets are relative to the data section start.
- `ReadFile` returns the complete raw payload and verifies Adler-32. There is
  no per-file cookie. Header-declared padding separates table and data.
- Read-only, seekable-stream based; never writes to the archive.
- Throws `NotSupportedException` for non-v1 paks (e.g. mod paks use
  `PAK\0`) instead of mis-parsing them.

### BatlParser (`Core/Atlas`)

- Grammar: `'BATL' { u8 pageLen, page, { u8 nameLen, name, u16 flags, 8×u16
  (x,y,w,h,offsetX,offsetY,origW,origH) }, 0x00 }`, next page after each `0x00`.
- A terminal empty page name ends the stream. The stock v35 atlas has three
  pages and **10,234 complete regions**, including `scissorsAtkC_04`.
  Older truncation claims came from the PAK reader dropping four payload bytes;
  see `NATIVE-RESOURCE-DECODER-CORRECTION.md`. Damaged trailing input is still
  counted explicitly in `DroppedTrailingRegions`.

### AtlasFrameComposer (`Core/Atlas`)

- SkiaSharp decodes page PNGs; each region is cropped and pasted top-left at
  (`OffsetX`,`OffsetY`) onto a transparent (`OriginalWidth`,`OriginalHeight`)
  canvas — verified **pixel-exact** against official `AtlasTool -Expand`
  output for every `AtkKatanaB2` frame (integration test).
- Emits UI-neutral `ClipFrame` records (RGBA bytes + duration); the UI layer
  never touches SkiaSharp.

### AnimationCatalog (`Core/Catalog`)

- Groups regions by `^(.*)_(\d+)$` → animation ID + numeric frame order;
  non-suffixed regions become single-frame entries. Reports numbering gaps.
- `Search` is case-insensitive substring over IDs. Accepts an optional
  timing overlay keyed by animation ID.

### AnimationLoader (`Core/Sources`)

- `FromResPak(resPak, atlasEntry, timing?)`: index → extract → parse →
  compose. Direct archive reads; no full-pak extraction step.
- `FromDirectory(dir, atlasFile, timing?)`: same pipeline from an
  `AtlasTool -Expand`-style folder.
- `LoadClip(id, fps, perFrameDurations?)`: ordered composed frames with
  uniform durations (default 30 FPS, see timing note) + attached overlay.
- `LoadTimingOverlay(path)`: parses the hand-authored JSON overlay.

### PlaybackEngine (`Core/Playback`)

- Deterministic, UI-free: `Load/Unload/Play/Pause/Restart/StepNext/
  StepPrevious/SeekFrame/Scrub(fraction)/Tick(delta)`.
- `Loop`, validated `Speed > 0`, per-frame `Duration`s (non-uniform ready).
- Host drives `Tick` (the app uses a 16 ms `DispatcherTimer` + `Stopwatch`
  delta); `FrameChanged` notifies the view.

### GameSourceLocator (`Core/Sources`)

- Steam registry (`HKLM\SOFTWARE\[Wow6432Node\]Valve\Steam`), then
  `libraryfolders.vdf` libraries, then common drive paths — no hardcoded
  usernames. Returns the first directory containing `res.pak`.

## UI (`DcAnim.App`, Avalonia 11 + Fluent dark + violet accent)

- **Left — Asset Browser:** search box, `N / M animations` count, `ListBox`.
- **Center — Preview:** custom `PreviewControl` (checkerboard + centered
  frame, `BitmapInterpolationMode.None`, 1–8x zoom) + transport bar
  (restart/prev/play/next, loop, speed 0.25–4x, FPS 12–60, zoom).
- **Right — Inspector:** ID, frames, duration, current frame, hit frame,
  charge, FX, timing source, asset source.
- **Bottom — Timeline:** custom `TimelineControl` (frame cells, playhead,
  hit marker, ruler, click/drag scrub via `ScrubRequested`).
- **Menu:** File (open res.pak / atlas folder / reload / exit), Help (about
  + attribution). Status bar reports the active source or actionable errors.
- `FrameBitmap.ToWriteableBitmap` converts RGBA → premultiplied BGRA once
  per frame and caches per clip.
- `--evidence <dir>` CLI: loads `AtkKatanaB2`, scrubs mid-clip, saves a
  window screenshot + decoded frame PNGs (used for `docs/evidence/`).

## Timing model & assumption

No per-frame durations were found in native resources (atlas, tracks,
ModTools docs). Playback therefore defaults to a uniform **30 FPS** with a
user-adjustable FPS/speed control, and the model already carries per-frame
durations so real timings can be dropped in later (CDB `charge`/`hitFrame`
analysis, code research, or per-clip overrides). The shipped
`timing-overrides.json` covers only hand-verified Katana links and is
explicitly labeled as an overlay, not vanilla data.

## Extension points (future milestones)

- `AnimationTiming` → full CDB weapon-row import (user-supplied CDB).
- `beheaded_tracks.json` bone tracks → hitbox/weapon-bone overlay layer.
- `PlaybackEngine.Tick` + per-frame durations → multi-track timeline,
  keyframes, audio/effect tracks, sequencing.
- `AnimationLoader` → additional atlases (mobs, FX), custom art import,
  CDB export, project save/load, plugin format adapters.
