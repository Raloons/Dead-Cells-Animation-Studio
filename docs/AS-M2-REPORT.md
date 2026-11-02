# AS-M2 Report - Professional Workspace & UI/UX Overhaul

**Status: COMPLETE.** The application is now a CapCut-inspired professional
workspace. All AS-M1 functionality is preserved (decoder, catalog, playback,
tests green); the UI was rebuilt around it as composable views.

## 1. New workspace layout

```text
┌ Top bar: identity · File Edit View Help · source label ──────────────┐
│ LIBRARY (resizable) │ VIEWPORT + transport │ INSPECTOR (resizable)    │
│ search/categories/  │ title · checker/fit/ │ Animation/Timing/        │
│ list+grid thumbs    │ zoom · canvas · pan  │ Source sections          │
├ Bottom TIMELINE (resizable height): toolbar + filmstrip ─────────────┤
│ ruler · frame cells + thumbnails · playhead · hit marker · scrub/zoom │
└ Status bar: source/selection status · shortcut hints ────────────────┘
```

Evidence (real running app, `--evidence [--size WxH] [--grid]`):
`as-m2-1920x1080.png`, `as-m2-1366x768.png`, `as-m2-2560x1440.png`,
`as-m2-grid-view.png` under `docs/evidence/`.

## 2. What changed (App only - Core decoders untouched)

- **Asset library** (`Views/AssetBrowserView`): search, category pills with
  counts (All 572 · Attacks 388 · Movement 31 · Idle 50 · Other 103, derived
  from verified ID naming patterns, documented in
  `Core/Catalog/AnimationCategorizer`), list/grid toggle, lazy background
  first-frame thumbnails (`Services/ThumbnailService`, single-frame decode -
  no eager 572-clip load), virtualized list, empty/filtered/no-source states.
- **Viewport** (`Views/AnimationViewportView` + `Controls/PreviewControl`):
  checkerboard toggle, fit-to-view (auto on select/layout, `Ctrl+0`), drag
  pan, 1–8x nearest-neighbor zoom, title/subtitle.
- **Transport** (`Views/TransportControl`): unified monochrome vector icon set
  (`Resources/Icons.axaml`, original artwork) - first/prev/play-pause/next/
  last, uniform 34 px buttons, violet primary on Play (switches to Pause),
  hover/pressed/disabled states, speed 0.25–4x, FPS 12–60, tooltips.
- **Inspector** (`Views/AnimationInspectorView`): collapsible Animation /
  Timing / Source sections, aligned label/value grid, read-only styling.
- **Timeline** (`Views/TimelineView` + `Controls/TrackControl`): time ruler
  with frame+time labels, filmstrip cells with thumbnails, selected highlight,
  played dimming, orange hit marker, white playhead with handle,
  click/drag scrub, zoom slider + fit, Ctrl+wheel zoom, horizontal scroll,
  auto-fit on load, position readout. Single-animation model; the track
  control is sized to grow into multi-clip lanes later.
- **Top bar**: File (open res.pak/atlas, reload F5, exit), Edit (play/pause,
  frame steps, first/last), View (checkerboard, fit, timeline zoom,
  reset layout), Help (native format notes, about). No fake Export/Share/AI
  buttons - every control performs a real action.
- **Resizing + persistence**: GridSplitters for library/inspector widths and
  timeline height; `Core/Services/WorkspaceSettings` persists to per-user
  `%AppData%` JSON with clamping; reset via View menu; evidence runs never
  overwrite settings.
- **Shortcuts**: Space, ←/→, Home/End, Ctrl+F, Ctrl+0, Ctrl+B, Ctrl+O,
  Ctrl+wheel timeline zoom - ignored inside text inputs/menus.
- **Theme** (`Theme/WorkspaceTheme` + `WorkspaceStyles`): charcoal surfaces,
  violet accents, shared text/button/category styles, focus/hover/pressed/
  selected/disabled states.

## 3. Core additions (additive, tested)

- `Catalog/AnimationCategorizer` (+`AnimationCategory`): Idle → Attacks →
  Movement → Other precedence, e.g. `idleGunA` is Idle, `combAtkA`/`gunA`/
  `HeavyBowShoot`/`rapierCombo`/`dualAxeSequence`/`lethalHit` are Attacks,
  defensive `block*` stays in Other (documented, not guessed).
- `AnimationLoader.LoadFrame`: single-frame decode for thumbnails; parity
  with `LoadClip` covered by test.
- `Playback/TimelineMath`: frame↔pixel mapping, label intervals, time
  formatting - pure and unit-tested.
- `Services/WorkspaceSettings`: load/save/clamp round-trip tests.
- Performance: bulk-pixel composer + SkiaSharp thumbnail path (per-pixel
  loops removed); 572-thumbnail prefetch completes in the background without
  blocking the UI.

## 4. Verification

- `dotnet test`: **78/78 pass** (incl. pixel-exact BATL test with
  `DCANIM_ATLASTOOL_REF`, native catalog/clip tests, 20+ new AS-M2 tests).
- Real-app screenshots inspected at 3 resolutions + grid view; refined across
  iterations (fixed: selection/load desync, thumbnail channel swap,
  viewport/timeline auto-fit, ruler crowding).
- Confirmed: native playback, selection, search, category filters, play/pause
  icon swap, five transport actions, scrub, timeline zoom, hit marker,
  inspector updates, panel splitters, usable at 1366×768, no clipped controls.
- No game files modified; no assets added to the repo; no remote configured.

## 5. Known limitations / AS-M3 candidates

- Thumbnail strip shows full-frame padding (no whitespace auto-crop yet).
- Single-clip timeline (multi-track/audio/keyframes are designed extension
  points, not implemented).
- `hitFrame` 0/1-based convention still undocumented upstream (marker is
  indicative); uniform-FPS assumption unchanged from AS-M1.
- Next: CDB weapon-row import for full timing, bone-track overlay layer,
  clip sequencing + export, custom art import.
