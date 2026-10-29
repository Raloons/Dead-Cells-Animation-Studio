# AS-M2.2 Report — Final UI Refinements

**Status: COMPLETE.** Two targeted changes on the approved AS-M2.1 design;
no restyling, no behavior changes beyond the specified items.

## 1. Bottom status bar removed

- Deleted the entire status `Border` from `MainWindow.axaml` (bar + text).
  The workspace and timeline reclaim the ~30 px.
- Feedback preserved contextually (no relocation of the persistent message):
  - Loading/empty states: the asset library's empty text already explains
    "No animation source loaded…" / "No animations match this filter";
    `AutoLoad` now calls `Browser.Clear()` so that guidance is visible.
  - Errors (bad pak/atlas, failed animation load, auto-detect failure):
    transient top-center toast over the viewport, auto-dismissing after
    6 s (`ShowToast` + single-shot timer). Reserved for problems only —
    successful loads need no announcement (browser populates, top bar shows
    the source).
  - Non-critical progress (thumbnail prefetch): library footer line via
    `AssetBrowserView.SetFooterStatus` ("Loading previews… N / 572", then
    "572 animations"). All `SetStatus` call sites and the `StatusText`
    element are gone.

## 2. Timeline lattice grid

- `TrackControl` now renders an editor-style lattice behind the filmstrip:
  thin low-contrast vertical lines on every frame boundary (minor,
  `#222227`) plus slightly brighter lines on labeled intervals (major,
  `#313138`), full-width lane separators top/bottom (`#2C2C33`), all on the
  neutral charcoal track background. No gradients, glow, or decoration.
- Alignment is structural, not visual: grid, ruler, cells, playhead and hit
  marker all derive from the same `TimelineMath` frame mapping
  (`XForFrame`/`FrameAtX`), so the lattice stays aligned under zoom and
  horizontal scroll. New pure helper `TimelineMath.GridLines` (minors,
  majors, viewport coverage including unused space) with unit tests for
  alignment and degenerate input.
- Ruler remains the spacing authority: label interval already accounts for
  label text width (verified no overlap at min zoom). Hit marker keeps its
  single muted amber tick, now centered and width-capped (24 px) so it stays
  subtle at high zoom.
- Horizontal separators span the full track width and are drawn per-lane,
  ready for future audio/effects/video tracks (no multi-track faked).

## 3. Verification

- `dotnet build` + `Release publish`: 0 errors. `dotnet test`: **80/80**
  (78 prior + 2 `GridLines` tests; pixel-exact BATL test unchanged).
- Real-app screenshots inspected: 1920×1080, 1366×768, 2560×1440, grid
  view, min-zoom timeline —
  `as-m2.2-1920x1080.png`, `as-m2.2-1366x768.png`, `as-m2.2-2560x1440.png`,
  `as-m2.2-grid.png`, `as-m2.2-timeline-minzoom.png`.
- Checked: no status bar remnant; grid alignment while scrubbing (playhead
  on frame boundaries) and scrolled; no ruler overlap; lattice visible but
  unobtrusive; no clipping at 1366×768; selection/inspector/transport
  unchanged; shortcuts, resize, persistence intact.

## 4. Known limitations

- Toast is single-slot (latest message wins); sufficient for rare load
  errors. No toast queue.
- Min-zoom ruler shows only the frame-0 label for short clips (clean by
  design; minor ticks still mark every frame).
- Next: CDB weapon-row timing import, bone-track overlay, sequencing/export.
