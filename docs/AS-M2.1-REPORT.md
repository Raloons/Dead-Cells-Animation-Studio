# AS-M2.1 Report — Professional Visual Redesign

**Status: COMPLETE.** The workspace now reads as a mature, neutral desktop
editing tool. No purple remains; selection, transport, sliders, timeline and
panels share one restrained gray system with a single muted amber reserved
for the hit-frame marker. All AS-M2 functionality is preserved.

## 1. Visual changes

- **Accent removal.** `SystemAccentColor` (Fluent) and all workspace tokens
  moved to neutral grays (`#8E8E96` family). This single change neutralizes
  checkboxes, sliders, focus rings, text selection and default list selection
  app-wide while keeping them fully usable.
- **Title bar.** Purple dot removed; text-only identity
  (`Dead Cells Animation Studio`) + File/Edit/View/Help. Menus unchanged.
- **Selection system.** Library rows: `#2E2E36` fill on selection, subtle
  hover, no accent border. Category tabs: text-only pills → flat neutral
  tabs (transparent idle, gray fill + gray border when active, square 3 px
  corners). Grid selection uses the same ListBox mechanism.
- **Transport.** Violet primary block removed; Play/Pause is now a raised
  neutral button (`#202025` + strong border). Same unified vector icons.
- **Viewport.** Checkerboard darkened and lowered in contrast
  (`#232329`/`#19191E`) so it no longer dominates; solid dark fallback via
  the existing toggle. No rendering changes to native frames.
- **Timeline (priority).** Denser lane (56 px), slimmer ruler (26 px),
  major/minor ticks, label interval accounts for real text width so labels
  never overlap at any zoom. Thin 1.5 px light playhead with small ruler
  handle through the full track. Selected frame = muted fill + 1.5 px gray
  outline (no violet wash). Hit marker = centered amber tick capped at 24 px
  wide — identifiable at every zoom without becoming a block. Toolbar reduced
  to position readout + zoom + fit (legend and decorative icon removed).
  Auto-fit on load/layout with user-zoom memory (programmatic vs manual zoom
  distinguished so layout passes don't fight the user).
- **Status bar.** Persistent shortcut reminder deleted. Only contextual
  status (source, selection, loading progress, errors) remains.
- **Inspector.** Expanders flattened (transparent, hairline separators);
  label/value grid, spacing and type scale unchanged (11/12/13 px).
- **Shortcuts.** Still fully functional; discoverable via tooltips, menu
  gestures, and a new **Help > Keyboard shortcuts** dialog.
- **Typography/spacing.** No new fonts; 4 px grid, 3–4 px radii, square-ish
  controls throughout; pill shapes eliminated (category tabs, badges).

## 2. Verification

- `dotnet build` (App) + `dotnet publish -c Release`: 0 errors.
- `dotnet test`: **78/78 pass** (pixel-exact BATL test included; no test
  changes needed — redesign is presentation-only).
- Real-app screenshots inspected at 1920×1080, 1366×768, 2560×1440, grid
  view, and timeline min/auto zoom:
  `as-m2.1-1920x1080.png`, `as-m2.1-1366x768.png`, `as-m2.1-2560x1440.png`,
  `as-m2.1-grid.png`, `as-m2.1-timeline-minzoom.png`.
- Iterated on: thumbnail channel swap (unchanged from AS-M2), hit-marker
  chunkiness at high zoom, ruler-label overlap at min zoom, timeline
  auto-fit sticking at intermediate layout widths.
- Confirmed: no purple accents, no gradients/glass/glow/cards, no clipped
  controls at 1366×768, scrub/step/zoom/fit/playback/inspector/resize/
  persistence all intact, no game files touched, no assets added to repo.

## 3. Known limitations

- Ruler at minimum zoom shows sparse labels (only frame 0 for an 11-frame
  clip) — clean by design; minor ticks still mark every frame.
- Timeline track caps at 128 px/frame; very wide windows leave empty track
  surface (honest editor surface, reserved for future tracks).
- `hitFrame` 0/1-based convention still undocumented (marker indicative);
  uniform-FPS assumption unchanged from AS-M1/AS-M2.
- Next: CDB weapon-row import, bone-track overlay, clip sequencing/export.
