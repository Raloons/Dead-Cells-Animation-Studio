# AS-M1 Report — Foundations & Native Animation Playback

**Status: COMPLETE.** All Definition-of-Done items are met; no blocker remains.
A genuine Dead Cells hero animation (`AtkKatanaB2`, Katana family) is decoded
from the stock game archive and played back in a launchable desktop app.

## 1. What was built

- Standalone solution (`DeadCells.AnimationStudio.slnx`):
  `src/DcAnim.Core` (UI-free decoders + playback), `src/DcAnim.App`
  (Avalonia desktop UI), `tests/DcAnim.Tests` (xUnit v3, 33 tests).
- Launchable app: `scripts/run.cmd` or `dotnet run --project src/DcAnim.App`.
- Real pipeline, no placeholders: `res.pak` → PAK index → BATL atlas →
  page PNGs → composed RGBA frames → 572-animation catalog → playback.

## 2. Native resource discoveries

**2026-10-30 correction:** the PAK reader's offset/traversal defect caused the
original truncation conclusions. The table below is corrected from complete,
checksum-verified resources; see `NATIVE-RESOURCE-DECODER-CORRECTION.md`.

| Question | Answer (with evidence) |
|---|---|
| Hero animation storage | `atlas/beheaded.atlas` (binary BATL) + `beheaded0/1/2.png` in stock `res.pak`; 3 pages, **10,234 complete regions**, 572 animation IDs |
| ID → visual mapping | Region runs `AnimName_00…NN` in numeric order; each region is one full-body frame cropped at `(x,y,w,h)` and pasted top-left at `(offsetX,offsetY)` onto an `(origW,origH)` canvas |
| Frame durations | Not present in atlas/tracks/docs; uniform **30 FPS default** (user-adjustable 12–60, 0.25–4x), per-frame durations modeled for later real data |
| Assembly/transforms | None needed — frames are pre-rendered; no rotation/scale metadata |
| Tooling reused | Official `PAKTool`/`AtlasTool` workflows from `ModsDoc.pdf`; `AtlasTool -Expand` output used as the pixel-exact reference for our decoder |
| BATL grammar | `'BATL' { u8 pageLen, page, { u8 nameLen, name, u16 flags, 8×u16 }, 0x00 }` next-page-after-`0x00`; see `docs/ARCHITECTURE.md` |
| PAK v1 grammar | `PAK\x01`, data offset, hash string, version, depth-first entries; file = `u32 offset/size/adler32`; complete raw payloads; data start 238,570. 7,055 files in stock `res.pak` |
| BATL tail | Final region `scissorsAtkC_04` is complete; terminal empty page name does not add another page |
| Bone tracks | `atlas/beheaded_tracks.json`: 572 animations, seven distinct track names overall; per-frame triplets, not 75 bones per animation |
| Timing overlay | Katana links from AAB CDB research: B2 hit 6 / B3 hit 5 / B4 hit 3, charge 0.2 s, `animSpd` 1, `fxBeheadedKatana*` — labeled as observed overlay, not vanilla claims |
| ModHelperSkin | Green debug mannequin (placeholder body), unsuitable as a visual target; investigated and rejected with evidence |

## 3. Verified animation IDs (sample; full catalog = 572)

`AtkKatanaA` 26 · `AtkKatanaB` 16 · **`AtkKatanaB2` 11** · `AtkKatanaB3` 11 ·
`AtkKatanaB4` 16 · `LoadAtkKatanaA` 13 · `LoadAtkKatanaB` 16 — all gap-free,
integration-tested against the real archive.

## 4. Evidence

- `docs/evidence/app-mainwindow.png` — running app: browser (572/572),
  genuine `AtkKatanaB2` frame 5/11 on checkerboard, inspector (hit 6,
  charge 0.20, FX, timing + asset source), timeline with playhead + hit
  marker, transport controls. Captured via the app's own `--evidence` mode.
- `docs/evidence/AtkKatanaB2-contact.png` — all 11 decoded frames in order.
- `docs/evidence/AtkKatanaB2.gif` — animated sequence (slowed for visibility).
- Tests: 33 pass (31 unit/format + 5 native integration); pixel-exact
  composer test vs official AtlasTool output passes with
  `DCANIM_ATLASTOOL_REF` set; native tests skip cleanly without a game install.

## 5. Build & test results

- `dotnet build` (Core + App): 0 errors. Only warning is upstream
  `NU1903` for `Tmds.DBus.Protocol` (transitive Avalonia dependency).
- `dotnet test tests/DcAnim.Tests`: **33 total — 32 passed, 1 env-gated skip
  by default; 33/33 with the AtlasTool reference dir.**
- App launches, auto-detects `res.pak`, loads `AtkKatanaB2` by default;
  play/pause/step/scrub/speed/FPS/zoom verified through the evidence run.
- Verified no-ops: no game files modified (archive opened read-only;
  SHA untouched), no assets copied into the repo, no remote configured.

## 6. Technology decision record

Preferred stack (C# / .NET + Avalonia + SkiaSharp) was adopted because
network/package restore was verified working — but only where justified:
SkiaSharp decodes/composes in Core; the UI converts to `WriteableBitmap`
and stays renderer-agnostic. No Avalonia-for-its-own-sake coupling in the
decoder or playback layers.

## 7. Known limitations

- Uniform-FPS assumption until real per-clip timings are sourced.
- `hitFrame` 0/1-based convention is undocumented; the timeline marker is
  indicative (stated in-app and in code).
- Normal maps (`*_n.png`) ignored — correct for flat 2D preview.
- Windows-only packaging target for AS-M1 (Core itself is cross-platform).
- Single-clip viewer: no multi-track, keyframes, audio, sequencing yet —
  all have designed extension points (`docs/ARCHITECTURE.md`).

## 8. Next milestones (candidates)

1. Full CDB import (user-supplied): per-link timing/FX/sound across weapons.
2. Bone-track overlay: weapon-bone + hitbox visualization from tracks.json.
3. Timeline sequencing: multi-clip arrangement, exportable configs.
4. Custom art import + project save/load + format-adapter plugins.

## 9. Reproduction

```powershell
.\scripts\run.cmd
dotnet test tests/DcAnim.Tests
$env:DCANIM_ATLASTOOL_REF = '<AtlasTool -Expand dir>'
dotnet test tests/DcAnim.Tests --filter "FullyQualifiedName~NativeBeheadedTests"
```
