# AS-M4.2 Report — Animation Splicing & Frame-Level Editing

**Status: COMPLETE.** Clips are now windows onto native animations: trim to
arbitrary source ranges, split at the playhead, copy/cut/paste/delete frame
ranges across animations, reverse clips, freeze poses with holds, edit on a
frame-accurate timeline with onion skins, and export everything pixel-identical
to the preview. All non-destructive (source references only), all undoable,
format v3 (v1/v2 still load).

## 1. Frame indexing convention

- **Model (stored): 0-based inclusive.** `SequenceClip.SourceFirst`,
  `SourceLast` (null = last available frame). Holds are output-frame counts.
- **UI (displayed): 1-based inclusive.** Inspector "Source In/Out" show
  `first+1 … last+1` ("Frames 4–8 of 26"); timeline per-frame numbers are
  1-based source indexes; the ruler/position readout stays 0-based-global
  (`Frame 1 / N` = first output frame, pre-existing convention).
- Timeline/playhead positions are 0-based global output frames (unchanged).

## 2. Timing model

`SequenceTiming.SpliceMap(First, VisibleCount, Reversed, LeadHold,
BaseOutputs, TailHold)` is the single choke point mapping output frame `k`
to a 0-based source frame: lead repeats of the first displayed frame, then
resampled base frames (`SourceIndex`), then tail repeats of the final
displayed frame; reversed flips the direction. The renderer
(`SequenceCompositor.Render`), sprite-sheet metadata, viewport overlay,
timeline labels and keyframe remapping all consume it — preview, exports,
labels and undo agree by construction.

`VisibleSourceFrames` clamps trims into the available count; `Resolve`
skips zero-visibility clips (missing animations were already skipped).
`ClipOutputFrames` gains hold terms. `TimelineMath.OnionFrames` computes
clamped, no-wrap neighbor indexes for onion skins (previous far-to-near).

## 3. Operations and policies

- **Trim** (inspector numerics or drag clip edges, hover cursors): sets
  `[first..last]`; keys re-anchor to sources (see §4); out-of-range keys
  drop with a toast count (undo restores). Left-edge drag maps the drop
  output frame back through the current mapping, so what you grab is what
  you trim to.
- **Split** (Ctrl+K, Edit menu, context menu, toast on no-op): snaps forward
  to the next source boundary so halves cover whole source runs; the left
  half keeps the id, the right gets a new one; playhead/select move to the
  right half. Halves keep speed/direction; holds stay at their original end
  (tail→right, lead→left).
- **Copy/cut/paste/delete/duplicate** operate on the frame selection if
  present, else the selected clip, else toast. Paste splits the host clip
  and inserts; delete trims/removes per overlapped clip. All single-undo via
  edit groups (no-op groups leave no history entry).
- **Reverse** (R, menu, checkbox): flips display order; keys mirror with the
  mapping so motion reverses in sync.
- **Hold** (inspector numeric, context submenu 2/6/12/30/Clear): appends
  repeats of the final displayed frame; efficient (no pixel duplication).
  Keys in the hold zone keep their offset from the end.
- **Frame selection** (Shift+drag, Shift+click for one frame, Esc clears):
  rendered as a highlight across ruler + clip lane; Delete/Ctrl+C/X/V/D
  prefer it over the clip target. Cleared/validated on every timeline
  refresh, so New/Load/delete can never strand it.
- **Ruler click seeks** to the exact frame (new; sequence timeline had no
  direct seeking before).

## 4. Keyframe policy (documented, tested)

Keys are output-local but **anchored to their source frame**: every structural
edit re-maps each key to the first new-local frame showing the same source
(last wins on collision); keys whose source left the visible range are
dropped and counted. Consequences:

- Trim/split/delete preserve the intended motion; split additionally pins
  **boundary holds** at the seam with the original evaluated values *and the
  original previous-key interpolation*, so a Linear span crossing the cut
  continues with the identical slope (verified sample-exact at 1×).
- Reverse mirrors key times; holds extend/shrink duration without moving base
  keys; hold-zone keys follow the end offset.
- Copy/paste re-bases keys into the new clip with the same relative motion.

## 5. Resampling caveat (honest limitation)

Trims/splits/deletes/pastes are *source*-defined while outputs are
*speed*-resampled. At 1:1 mappings (e.g. speed 1× at 30 fps — the splicing
sweet spot) every operation is output-exact (pinned by tests, including
seam-continuous motion). With non-1× speeds or non-native FPS, re-resolved
halves can differ by ±1 output frame at a seam; the timeline visibly shows
the true durations, nothing crashes, undo restores. Keyframe remap tests pin
bounded drift for speed 0.5×.

## 6. Ripple policy (documented, predictable)

Per-project **Ripple effects/audio** toggle (default ON, persisted in v3).
Any edit that changes total duration shifts effects (`StartFrame`) and audio
(`StartSeconds`) at or after the edited clip's start by the delta; reverse,
split and no-op edits shift nothing. FPS changes never ripple (grid change,
not an edit). Absolute-point rule: everything at/after the point moves,
including effects overlapping the edited clip — simple and predictable.

## 7. Onion skins (preview-only)

Ghosts come from the already-composed bitmap cache (no re-decode, respects
holds/reverse/boundaries, clamps at ends). Past = desaturated slate-blue,
future = warm sand, drawn under the base frame at 0.15–0.6 opacity, 1–3 per
side. Settings live in the workspace file (global view preference);
per-frame neighbors recomputed every `RefreshFrame`, so scrubbing stays
correct. Exporters consume `_seqRendered` directly — ghosts can never leak
into PNG/GIF/sheet (asserted in-app).

## 8. Project format v3

Adds per clip: `sourceFirst` (default 0), `sourceLast` (nullable),
`reversed`, `leadHoldFrames`, `tailHoldFrames` (0..10000, validated), plus
project `rippleFxAudio` (default true). v1/v2 load with documented defaults
(full range, forward, no holds, ripple on). Load rejects structural nonsense
(negative first, last<first, holds out of range); out-of-range ends against
the *installed* game clamp on open with a toast + dirty marker (drift-safe,
never silent). Save is atomic (temp + move, pre-existing).

## 9. Verification

- **Unit: 170 passed, 1 pre-existing skip, 0 failed** (31 new `SpliceTests`:
  mapping/trim/split/reverse/hold/copy/paste/delete/ripple/groups/format-
  migration/clamp/onion; `DemoProject_LoadsAndRoundTrips` validates the
  shipped demo file).
- **In-app audit: 67/67 steps** (10 new splice steps driving the real UI
  methods: trim inspector+drag paths, split/reverse/hold, playback across
  splices, v3 save/reopen round-trip, copy/paste/delete, ripple on/off,
  onion enable/shots, selection lifecycle, format migration, export parity
  on spliced content). Notable catches during verification, all fixed +
  regressed:
  (a) phantom undo entries for no-op splits → `EndGroupIfChanged`;
  (b) stale frame selection validated against pre-rebuild totals → validate
  inside `RefreshSequenceTimeline` against fresh resolves;
  (c) offscreen captures need a forced layout pass or templated-control
  pixels lag the model by a frame in headless runs (`UpdateLayout()` in
  `AuditShot` — capture-only, no app behavior change).
- **Exports**: PNG/GIF/sheet asserted frame-count-exact on spliced
  (trimmed + reversed + held + split) sequences in-app; exporters share the
  composed frames (parity by construction).
- **Screenshots** (docs evidence via audit): `audit-04-split-hold.png`
  (split segments with `REV`/`+H`/trim labels), `audit-05-onion.png`
  (onion ghosts + per-frame source numbers), selection highlight states,
  final Katana sequence. Interactive paths (drag edges, Shift+drag, ruler
  click, menus, inspector numerics, toasts) verified in-app; headless limits
  (file dialogs, pixel-perfect human judgment) documented below.

## 10. Known limitations / not tested by hand

- Human-only paths (file pickers, discard prompt, export-dialog cancel) are
  code-inspected, not clicked; logic is straight-line early-returns.
- Audible output needs ears; scheduling/voice lifecycle is unit-tested.
- No unbounded fuzzing; stress is bounded (many-segment sequences in tests).
- Split seam is ±1-frame exact only under resampling (§5); lead holds exist
  for seam compensation but have no UI (documented internal).
- Per-frame source numbers draw only at high zoom (ppf ≥ 28, ≤ 240-frame
  clips) to bound label cost; virtualization otherwise unchanged.

## 11. Reproduction (if anything regresses)

- Cross-clip overlay crash class (AS-M4.1): select short clip, park playhead
  in a longer clip, play — covered by `overlay.cross-clip-selection`.
- No-op undo pollution: split at a clip edge repeatedly, then undo — the
  first undo must revert a real edit, never a phantom (covered).
- Stale selection: select frames, New Project — highlight must clear
  (covered by `splice.selection-state`).
- Headless screenshots must call `UpdateLayout()` before capture.
