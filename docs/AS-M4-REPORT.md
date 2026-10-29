# AS-M4 Report — Animation Authoring, Keyframes, Effects & Audio

**Status: COMPLETE.** The sequence editor is now an authoring tool: editable
transforms with Hold/Linear keyframes, independent effect layers, a WAV audio
track with synchronized preview, and full undo/redo — all on the approved
neutral UI, with preview and every exporter sharing one compositor.

## 1. Editing model (Core, UI-free)

- **Transform** (`Sequence/Transforms.cs`): X/Y, ScaleX/Y, Rotation°,
  Opacity 0..1, normalized Pivot, FlipH/V. Applied non-destructively;
  native BATL frames are never modified. Nearest-neighbor sampling keeps
  pixel art crisp. Matrix order `T(pos)·R·S·T(−pivot·size)`; unscaled
  rotation is plain numeric lerp (no angle unwrapping — deterministic and
  documented).
- **Keyframes**: per property, clip-local integer frames, Hold/Linear
  interpolation; before-first-key falls back to the base value; exact,
  continuous, and shared by preview and export (`KeyframeEvaluator`).
- **Composition** (`Sequence/Composition.cs`): one Skia renderer draws the
  resolved native frame + effect layers (ordered by Layer) onto the
  sequence canvas with straight-alpha compositing. Exporters and preview
  consume identical RGBA frames (parity by design, tested pixel-for-pixel).
- **Audio timeline** (`AudioTimeline`): pure math — audible clips at time t
  with file offsets, trim/EOF clamping, mute; playback lives in the app
  (`AudioEngine`, NAudio) with per-clip gain and reconcile-on-tick sync
  (no duplicate voices across seeks/loops; missing files toast once and
  are skipped).

## 2. Project format v2 (backward compatible)

- Adds clip transforms + keyframes, `effects[]` (image path, timing,
  layer, transform, keys), `audio[]` (path, start/trim/duration, volume,
  mute), optional canvas override. Asset paths serialize relative to the
  `.dcanim` file (forward slashes) and resolve on load.
- **v1 files open unchanged** (defaults filled; tested). Unknown versions
  are rejected with a clear error; unknown future keyframe properties are
  skipped, not fatal.
- Undo/redo is snapshot-based (JSON snapshots, cap 100): every mutation is
  covered uniformly, drags coalesce to one step (`BeginDrag`/`EndDrag`,
  `AbortDrag` restores), and the dirty flag compares snapshots — undoing
  back to the saved state clears `*`.

## 3. UI (approved design preserved)

- Timeline: animation lane (as before) + effect lane + audio lane with
  waveform; keyframe diamonds on the selected item; click-select, drag
  clips/effects/audio in time, drag keyframes, drop-to-insert with indicator,
  context menus, Delete key. Same lattice/ruler/playhead/zoom/scroll.
- Inspector: clip/effect/audio sections with transform numerics (commit on
  focus loss — no undo spam), keyframe list with add-at-playhead, prev/next
  navigation, per-row delete; relink buttons for missing assets.
- Viewport: drag-to-move the selection (coalesced undo), selection outline +
  pivot cross overlays that are never exported; drag on empty canvas pans.
- Menus/shortcuts: File additions (Add effect image…, Add audio), Edit Undo/
  Redo with labels, Ctrl+Z/Y (Ctrl+Shift+Z too), Help shortcuts extended.

## 4. Coordinate system

Sequence canvas = explicit project size, else max native frame extent (and
large enough for scaled effects). Native frames anchor top-left (identity
transform reproduces source pixels exactly); transforms place the pivot at
canvas-center + (X, Y). Overlays use the identical `TransformLayout` math as
the renderer, verified by unit tests. PNG/GIF/sheet all share it.

## 5. Export consistency

PNG/GIF/sheet exporters gained `ExportRendered` paths over composed canvas
frames (transforms + compositing included, checker/UI never included, audio
never embedded). GIF keeps its documented quantization/timing behavior.

## 6. Verification

- `dotnet build` + Release publish: 0 errors. `dotnet test`: **121/121**
  (99 prior + 22: keyframes, transforms, layout math, undo/redo, v1/v2
  round-trips, relinking, audio scheduling, composition, rendered parity).
- Real app: opened Katana project, keyframed B2 motion, placed a generated
  shockwave at the B2 strike with scale/opacity keys, placed a generated
  880 Hz tone at 0.5 s, previewed with audio sync, performed edits, undid
  and redid them, saved, closed, reopened (all edits + references intact),
  exported PNG (72), GIF (72 frames, 30 ms), sheet (1218×1370 + metadata) —
  all decoded with an independent reader. Missing-asset toast path exercised
  (screenshot shows it). Screenshots: `as-m4-sequence.png`,
  `as-m4-viewer.png`, `as-m4-combo.gif`, `as-m4-effect.png`.
- Audio limitation (documented, not claimed): headless CI cannot verify
  audible output; scheduling/sync/voice-lifecycle are unit-tested and the
  app degrades gracefully without an audio device.

## 7. Known limitations / next steps

- Single effect lane visual (layers order within it); no nested groups.
- GIF stays 256-color/1-bit-alpha with 10 ms timing steps.
- Next: full CDB timing import, bone-track overlay, transitions, nested
  tracks, custom art import.
