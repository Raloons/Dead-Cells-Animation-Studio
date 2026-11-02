# AS-M3 Report - Animation Sequencing, Project Saving & Export

**Status: COMPLETE.** The studio is now a functional sequencing editor:
multi-clip projects with per-clip speed, deterministic playback, `.dcanim`
persistence, and PNG/GIF/sprite-sheet export - with preview and all
exporters sharing one timing model and one coordinate system.

## 1. Project model (`.dcanim`, versioned JSON)

```jsonc
{ "format": "dcanim", "version": 1, "name": "Katana Combo", "fps": 30,
  "clips": [{ "id": "<stable guid>", "animation": "AtkKatanaB2", "speed": 1.0 }] }
```

- References only (animation IDs + speeds + fps + name); **no game
  artwork** in project files. Version gate rejects unknown versions with a
  clear error; structural validation rejects bad timing/duplicate ids;
  unknown animation IDs are warnings (reported, skipped) rather than load
  failures. `SequenceDocument` centralizes mutations, file association and
  unsaved tracking (`*` title marker, prompt on close/new/open).

## 2. Timing model (deterministic, integer timebase)

- Project FPS (default 30) × per-clip speed multiplier. Native source rate
  is the documented 30 FPS assumption (shared with AS-M1).
- Clip output frames `N = max(1, round(S · fps / (30 · v)))`; output frame
  `k` shows source `min(S−1, ⌊k·S/N⌋)` - holds when slow, samples when
  fast, boundaries exact, no drift. Implemented once in
  `Core/Sequence/SequenceTiming` and reused by the composer, ruler labels
  and exporters.

## 3. Sequence mode (beside viewer mode, never mixed)

- Top-bar **Viewer | Sequence** toggle; timeline, inspector and transport
  rebind per mode. Viewer workflow is untouched.
- Track: labeled clip blocks (id + frame count + first/mid/last thumbs),
  click select, drag-reorder with midpoint drop indicator, drag-from-library
  to append/insert (custom `dcanim-animation-id` payload, insert indicator),
  context menu (Duplicate / Remove / 0.25–2× speeds), Delete key.
- Single contiguous track; no overlapping clips (documented scope).
- Playback reuses `PlaybackEngine` over a composed virtual clip, so
  play/pause/step/scrub/loop/jump/speed UI is identical in both modes.
- Clip inspector (speed NumericUpDown + presets, frames, duration) and
  sequence inspector (name, FPS, clip count, total). Unsaved prompt on
  close/new/open; Ctrl+N/O/S/Shift+S.

## 4. Coordinate system

- Sequence canvas = max frame extent across the project; every frame
  centered (integer offsets), transparent elsewhere. Compositor, PNG, GIF
  and sheet all use `ExportCanvas.ComposeCentered`; preview draws the same
  composed frames. No per-frame recentering jitter; anchors documented as
  center-of-max-extent (future work: per-anchor overrides).

## 5. Exporters (all verified on real output)

- **PNG sequence** (`frame_%04d.png`, RGBA, full canvas, no checker/UI):
  refuses non-empty dirs unless overwrite is set; pixel-identical to preview
  (test compares every pixel).
- **Animated GIF**: 10 ms-quantized delays (`round(1000/fps/10)`; reported
  effective FPS, e.g. 30 FPS → 30 ms steps → 33.3 FPS effective), octree
  256-color, no dither, 1-bit transparency, loop flag; palette/timing limits
  stated, not hidden. Frame count and delays verified by decoding.
- **Sprite sheet**: row-major grid, configurable columns/padding, one cell =
  full canvas frame, plus JSON metadata (positions, dims, source anim, seq
  index, fps, clip boundaries). Generic art-tool interchange, not BATL.
- Export dialog: format, destination pickers, GIF loop, sheet columns/
  padding, overwrite flag, frame/duration estimate, progress bar, result or
  error text. Runs off-UI-thread with progress.

## 6. Verification

- `dotnet build` + `Release publish`: 0 errors. `dotnet test`: **99/99**
  (80 prior + 19 sequence/export tests; pixel-exact BATL test intact).
- Real app: built the Katana project (`AtkKatanaA → B2 → B3@0.5× →
  B4@1.25×` = 26+11+22+13 = 72 frames), played across boundaries, scrubbed,
  stepped, saved `katana-combo.dcanim`, closed, reopened, exported PNG
  (72 files, RGBA, transparent), GIF (72 frames, 30 ms, loop) and sheet
  (1218×1370 + metadata with exact boundaries) - all decoded with an
  independent reader (PIL) and pixel-checked. Screenshots:
  `as-m3-sequence.png`, `as-m3-viewer.png`, `as-m3-combo.gif`; tutorial
  project committed as `docs/evidence/katana-combo.dcanim`.
- Interactive drag-and-drop was code-reviewed but not pointer-driven in
  headless runs; drop-target math (`SequenceTiming.InsertIndexAt`) and the
  add/reorder paths it feeds are unit-tested, and the same path is exercised
  by the evidence save/reopen/exports.

## 7. Limitations / AS-M4 candidates

- Contiguous single track only; no overlap/blend, no audio/FX lanes (grid
  and lane separators are drawn extensible).
- Uniform-FPS + center-anchor assumptions unchanged from AS-M1.
- `hitFrame` markers are viewer-only for now.
- Next: full CDB timing import, bone-track overlay, clip transitions,
  multi-track audio/FX, custom art import.
