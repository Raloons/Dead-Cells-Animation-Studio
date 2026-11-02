# AS-M4.1 Stability Report - Workflow, State & Regression Audit

**Status: COMPLETE.** Full application-wide stability audit following the
user-reported Ctrl+Z crash on top of `9257e80`. All discovered crash classes
are fixed with regression coverage. Test suite: **139 passed, 1 skipped
(native atlas-tool parity, pre-existing skip), 0 failed**. In-app workflow
audit: **56/56 steps pass** with a clean process exit. No remote configured;
no game assets touched.

## 1. Root cause: the reported Ctrl+Z crash

**Undo on an empty history was already safe** (`Undo()` returns early when
`CanUndo` is false; extra Ctrl+Z/Ctrl+Y are no-ops - covered by
`undo-redo-empty`, `undo.drain-all-plus-extra`, `redo.drain-all-plus-extra`).

The crash family around it was **empty-sequence engine access during
selection/rebuild/transport transitions**:

- `SelectSeqTarget()` called `_engine.SeekFrame()` unconditionally. After
  `AddClip` → select → `RebuildSequence` on a still-unloaded engine (or after
  deleting the only clip), this threw
  `InvalidOperationException: No animation clip loaded` from
  `PlaybackEngine.EnsureClip()`. Reproduced in-harness as
  `clip.add-first+select+play` and `select.empty-engine-safe` failures.
- Transport-adjacent paths (`StepNext/Previous`, `SeekFrame`) throw by engine
  contract on an empty clip. All UI call sites were already guarded by
  `CanTransport` except the selection path above.
- Stale `_seqTarget` / `_seqSelectedClip` / `_seqKeyProp` survived project
  replacement, clip deletion, and undo/redo, so later rebuilds, viewport
  drags, and keyframe moves operated on dead ids.

**Fixes:**

- `SelectSeqTarget` ignores unknown ids and only seeks when
  `_seqClip is { FrameCount: > 0 }`, clamped into range.
- `RebuildSequence` clears any non-existent target for **all** kinds (was
  clip-only) and clears stale keyframe selection via `ValidateKeySelection()`.
- `Undo()`/`Redo()` clear stale targets, rebuild, and `AbortDrag()` any
  open gesture before time-travel.
- `MoveSelectedKeyframeTo` / `OnViewportMove` abort cleanly when the target
  vanished mid-gesture instead of throwing.
- Engine contract pinned by `EngineRejectsEmptyAndOutOfRange` (unit) and
  `engine.empty-clip-contract` + `viewer.empty-sequence-transport` (in-app):
  UI guards, engine throws - both sides asserted.

## 2. Other crashes / defects found and fixed

| # | Defect (reproduced) | Fix |
|---|---|---|
| 1 | Viewport showed a stale frame on empty sequences (`RefreshFrame` returned early only for null clip, not 0-frame) | 0-frame branch actively clears viewport, updates subtitle/overlay, syncs audio |
| 2 | `NaN`/`Infinity` transform/keyframe values accepted, then poisoned JSON snapshots (undo history, save files) | `RequireFinite`/`RequireFiniteKey` on all setters; `SetKeyframeValue` validates before lookup; serializer `ValidateTransform` + finite checks for audio times/volume |
| 3 | Loader silently coerced bad values (`speed<=0→1`, negative frames dropped, opacity clamped, volume clamped), so corrupt files loaded instead of being rejected | `FromDto` preserves stored values; `ValidateStructure` rejects them with clear messages |
| 4 | Duplicate clip/effect/audio ids loaded without error | `CheckId` duplicate detection; `DuplicateIdsRejected` + `Serializer_RejectsDuplicateIds` tests |
| 5 | Negative keyframe frames silently dropped on load | Kept and rejected by validation (`CorruptProjectRejected`, `Validate_RejectsCorruptAndDuplicateContent`) |
| 6 | Dragging the viewport fired `MoveStarted` (opening a coalesce group) on mere press; a lost release/capture leaked the group and corrupted the next undo | Gated `MoveStarted`/`KeyframeDragStarted` to real movement past threshold; `DragCancelled`/`MoveCancelled` on capture loss and re-entrant press; host aborts the group |
| 7 | `PreviewControl.OnPointerReleased` never fired `MoveFinished` for real drags (armed-flag inversion) - `EndDrag` skipped, coalesce leaked | Track `_moveStarted`; fire `MoveFinished` iff started |
| 8 | Transform editor allowed scale 0.01..∞ / position ±∞ → canvas explosion or invisible frames | Clamp position ±20000, scale 0.01..64 (opacity was already 0..1) |
| 9 | Oversize canvas (e.g. 5000px effect) would allocate gigabytes | `MaxCanvasDimension = 4096` refusal with clear error; `CompositionRejectsOversizeCanvas` test; `RebuildSequence` catches compose failures, keeps last valid state, toasts |
| 10 | `RebuildSequence` built bitmaps into live state; a mid-compose throw left a half-built timeline | Build fully off to the side, commit atomically, dispose partials on failure |
| 11 | Volume/mute changes left stale NAudio voices playing | Stop voices on volume/mute inspector changes (reconciled on next tick) |
| 12 | `Save` wrote directly to the destination - crash mid-write corrupts the project | Atomic save: temp file in same directory + `File.Move(overwrite:true)` |
| 13 | Dead `MigrateV1ToV2` API suggested a migration path that doesn't exist | Removed; v1 loads directly with documented defaults |
| 14 | Unhandled async/UI exceptions terminated the process silently | `CrashLog` + `AppDomain`/`TaskScheduler`/dispatcher handlers; error dialog with log path; app stays alive |
| 15 | Clicking a library category pill (e.g. Idle) with a filtered-out selection crashed in `ApplyFilter` (`ArgumentOutOfRangeException` in Avalonia's selection model) - root cause was in-place mutation of the live `_filtered` ItemsSource plus clearing selection after the swap | `ApplyFilter` now clears selection while indices are valid → detaches source → rebuilds while detached → reattaches and restores; user-reported, crash log confirmed, regression-covered by `viewer.category-switch-with-selection` (58 in-app steps) |
| 16 | Pressing play with 2+ clips of different lengths: per-frame `ArgumentOutOfRangeException('frameIndex')` storm from the selection overlay, forcing taskkill - overlay used the playhead clip's segment with the selected clip's animation, requesting out-of-range source frames | Overlay now uses the selected clip's own resolved segment and hides when the playhead is outside it; `CrashLog` gained a dialog storm guard (concurrent + 5 s cooldown) plus ms-resolution log names; user-reported, log confirmed, sensitivity-proven (`overlay.cross-clip-selection` fails on old code with the identical stack, passes on new) |

## 3. Workflow coverage matrix

**Verified through real UI interaction** (in-app `--audit` harness driving the
actual `MainWindow` methods, 56 steps, with `AuditInvariants` after each:
engine index validity, bitmap-cache parity, rendered-frame parity, no stale
selection):

- Startup/viewer: select+play+pause, step+seek+scrub (incl. out-of-range
  clamp), loop+fps+speed, search no-results+clear, list/grid toggle,
  mode-toggle-no-project, empty-sequence-transport, empty-engine contract
- Undo/redo: empty history, drain-all+extra (both directions), redo-cleared-
  by-new-edit, dirty-tracking (undo-to-saved clears `*`), history-cap,
  delete-final-clip+keyframe restore, coalesced-drag-single-step
- Clips: add-first+select+play, add-more+move+duplicate, speeds+fps,
  seek-boundaries, delete-middle+first+last, delete-only-clip+undo
- Transforms: set-all+extremes, invalid-rejected, invalid-fps+speed-rejected
- Keyframes: crud+navigate, invalid-rejected, UI path (add-at-playhead,
  move-via-handler, edit, select, delete, step)
- Effects: missing-file-skipped, corrupt-file, add+move+layer+transform+keys
- Audio: missing-file (graceful voices), full-cycle (move/trim/volume/mute/
  select/play/seek)
- Projects: save-reopen-roundtrip, malformed+json-errors (6 hostile files,
  project intact), missing-file, v1-loads, missing-anim-ids (toast, no crash)
- Timeline: zoom+scrub+seek, empty-scrub, panel resize
- Modes: switch-while-playing (sequence→viewer→sequence, playing)
- Drags: viewport-doclevel, viewport-UI-path (real handlers + throttle),
  keyframe-abort-restores, delete-target-mid-gesture (undo-equivalent),
  coalesced-drag accounting
- Exports: png-sequence, gif, sheet (counts asserted), empty-project
  (refused cleanly), invalid-dest (illegal chars rejected)

**Verified through automated tests** (`dotnet test`: 139 passed, 1 skipped):

- `StabilityRegressionTests` (12): non-finite transforms/keyframes/scalars,
  history cap, coalesced-drag single step, aborted-drag restore, corrupt +
  duplicate project rejection, engine empty/out-of-range contract, exporter
  empty rejection + cancellation honor, oversize canvas refusal
- `SequenceModelTests` additions (5): non-finite input, history cap,
  undo/redo symmetry, duplicate-id rejection, non-finite JSON rejection
- Full pre-existing suite (122 baseline incl. keyframes, transforms, layout,
  undo/redo, v1/v2 round-trips, audio scheduling, composition parity) still
  green

**Inspected in code only** (require a human + OS dialogs; logic reviewed,
handlers verified by reading, not clicked):

- File open/save dialogs: cancel leaves state untouched (early return);
  failed load toasts and keeps current project (`OpenProjectFileAsync`
  loads to temp var first); failed save toasts without marking saved
- Unsaved-changes prompt: Cancel aborts New/Open/Close; Save routes through
  `SaveProjectAsync` result
- Export dialog: cancel returns null settings (no-op); in-progress export
  runs on background thread with progress; failures report inside the dialog,
  app stays responsive
- Help/shortcuts/about dialogs, window resize/minimize/maximize, layout
  save/restore: standard Avalonia paths, no custom native hooks

**Not tested:** audible output on real hardware (headless has no audio
device; scheduling/voice lifecycle unit-tested, missing-file degradation
verified in-app); unbounded fuzzing (deliberately out of scope).

## 4. Actual UI verification performed

- `dotnet build` (App): 0 errors. `dotnet test`: 139/139 + 1 pre-existing
  skip. Release publish path unchanged from AS-M4 (no new dependencies;
  NAudio already approved).
- In-app audit launched from the built exe
  (`--audit <dir> --size 1920x1080`): 56/56 pass, `AUDIT_LOG.txt` shows no
  `[FAIL]`, process exits cleanly (exit code 0; backstop `Environment.Exit`
  only for headless linger).
- Screenshots inspected: `audit-01-empty-sequence.png` (clean empty state,
  "No animation loaded", drop-target hint, no stale frame), `audit-02-
  populated.png`, `audit-final-state.png` (populated timeline renders,
  transport advances). One screenshot caught a transitional subtitle string
  while the timeline already showed the new clip - cosmetic refresh timing
  only; no stale selection or crash, invariants held. Noted as a watch item,
  not a defect.
- Export outputs from the audit (`exp-png/`, `exp.gif`, `exp-sheet.png +
  .json`) written and frame/clip counts asserted in-harness; independent
  decode was established in AS-M4 and exporter logic is unchanged except for
  the added empty/cancel guards (unit-tested).

## 5. Known remaining issues / limitations

- Screenshot-subtitle timing (above): cosmetic only, no state corruption.
- Audio: audible-output verification still needs a human with speakers; code
  degrades gracefully without a device.
- Human-dialog paths (file pickers, discard prompt, export dialog cancel,
  help dialogs, OS window chrome) are code-inspected, not clicked in this
  pass - handlers are straight-line early-returns with no state mutation
  before confirmation.
- Single effect-lane visual, GIF 256-color/10 ms steps, no native BATL
  export: unchanged AS-M4 limitations, out of scope for a stability
  milestone.

## 6. Reproduction steps (for anything that recurs)

- Engine-guard family: New Project → add clip → select it → delete only clip
  → Ctrl+Z/Ctrl+Y repeatedly → play/scrub. Before fix: `EnsureClip` throw.
  After fix: silent no-ops + valid empty state (covered by
  `select.empty-engine-safe`, `clip.delete-only-clip+undo`,
  `undo.drain-all-plus-extra`).
- Stale drag: start viewport drag → undo the target away mid-gesture (or kill
  pointer capture) → release. Before fix: throw or leaked coalesce group.
  After fix: abort + rebuild (covered by `drag.delete-target-mid-gesture`,
  `drag.keyframe-abort-restores`).
- Poisoned file: hand-edit `.dcanim` with `NaN`, negative frame, duplicate
  id, or `fps: 0` → open. Before fix: silent coercion or corrupt state.
  After fix: clear `InvalidDataException` naming the problem, current project
  untouched (covered by `project.malformed+json-errors`,
  `Serializer_Rejects*`, `CorruptProjectRejected`, `DuplicateIdsRejected`).

## 7. Build and test results

- `dotnet build src/DcAnim.App/DcAnim.App.csproj`: **succeeded, 0 errors**
  (30 warnings, all pre-existing NuGet vulnerability + xUnit analyzer notes).
- `dotnet test tests/DcAnim.Tests/DcAnim.Tests.csproj`: **139 passed,
  1 skipped, 0 failed** (140 total; skip is the pre-existing native
  atlas-tool pixel-parity test requiring the external tool).
- In-app audit: **58 passed, 0 failed** (incl. `viewer.category-switch-with-selection`, `overlay.cross-clip-selection`), clean exit.
