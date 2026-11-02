# Installing and running Dead Cells Animation Studio

Two ways to run it: **download the ready-made `.exe`** (no tools, no game
install needed) or **build it yourself** from source.

---

## Option 1 - Download the single-file `.exe` (easiest)

1. Open the repository's **Releases** page:
   https://github.com/Raloons/Dead-Cells-Animation-Studio/releases
2. Under the latest release, download **`DeadCells.AnimationStudio.exe`**.
3. Put it anywhere you like (Desktop, `C:\Tools\`, a USB stick - it does not
   matter, nothing is installed).
4. **Double-click it.** That's it.

There is nothing to install, no .NET runtime required, and no game files
needed - the default Beheaded animation set is embedded in the executable and
extracted to
`%LOCALAPPDATA%\DeadCellsAnimationStudio\bundled\v1\` on first launch.

> **Windows SmartScreen:** this is an unsigned hobby build, so Windows may show
> "Windows protected your PC". Click **More info → Run anyway**. Unsigned
> executables trigger this even when they are harmless.

> **Antivirus:** single-file .NET executables self-extract at runtime, which
> some scanners flag as suspicious. If yours does, build from source (Option 2)
> or add an exclusion.

### Optional: use your own game files

The bundled set covers the **default Beheaded outfit** only. To get every
outfit and the exact version you own, either:

- keep a normal Dead Cells install and the app finds `res.pak` automatically, or
- use **File → Open res.pak…** and pick
  `…\Steam\steamapps\common\Dead Cells\res.pak`.

The app only ever opens `res.pak` **read-only**; it never writes to your game
or saves.

### Command line (optional)

```powershell
# Force the embedded default-outfit set even if a game install is detected
DeadCells.AnimationStudio.exe --bundled

# Headless self-test: writes screenshots + AUDIT_LOG.txt to a folder and exits
DeadCells.AnimationStudio.exe --bundled --audit C:\temp\as-audit
```

---

## Option 2 - Build from source

Needed only if you want to modify the app or avoid SmartScreen.

**Requirements**

- Windows 10/11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

**Run it**

```powershell
git clone https://github.com/Raloons/Dead-Cells-Animation-Studio.git
cd Dead-Cells-Animation-Studio
.\scripts\run.cmd
```

`run.cmd` is just a shortcut for:

```powershell
dotnet run --project src/DcAnim.App -c Release
```

**Make your own single-file `.exe`**

```powershell
.\scripts\publish.cmd
```

Output: `publish\DeadCells.AnimationStudio.exe` (~69 MB, self-contained).
Copy that one file wherever you want.

Alternatively publish with the CLI directly:

```powershell
dotnet publish src/DcAnim.App/DcAnim.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
```

> Want a smaller file? Drop `--self-contained true` (→ ~10 MB) but then every
> user needs the .NET 10 Desktop Runtime installed.

---

## First five minutes

1. The left panel lists every animation (572 for the default outfit). Use the
   search box or the category pills.
2. Click an animation to preview it. Play/pause with **Space**, step with the
   arrow keys.
3. Switch to **Sequence** mode (top right) to build a multi-clip project:
   drag animations from the library onto the timeline.
4. Select a clip and use the right-hand inspector: trim In/Out, speed, hold,
   transforms, keyframes, effects and audio.
5. For weapon swapping, open the **WEAPON** section, pick a donor animation in
   the gallery (hover to preview), then **Auto-detect**.
6. **Ctrl+S** saves a `.dcanim` project. Export from **File → Export sequence**
   as PNG frames, GIF or a sprite sheet.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| SmartScreen blocks it | More info → Run anyway, or build from source |
| "No animation loaded" / empty library | Ensure `bundled\beheaded\` shipped next to the exe, or open `res.pak` manually |
| Library looks orange/yellow | That is the raw indexed atlas; in-game palette shading is not applied in the editor |
| Only default outfit available | Open your own `res.pak` to load outfit-specific atlases |
| Blurry sprite | Zoom is integer-scaled; use **Fit** (Ctrl+0) or the zoom slider |
| Crashes on start | Delete `%LOCALAPPDATA%\DeadCellsAnimationStudio\` and relaunch |

---

## Notes on the bundled art

`bundled/beheaded/` contains the default-outfit `beheaded.atlas` and its three
page PNGs, extracted unmodified from Dead Cells v35.

These assets are **third-party copyrighted material belonging to Motion Twin /
Evil Empire**. They are included here for fan modding and educational use only,
and are **not** covered by this repository's MIT license, which applies only to
the original application code and tools. See the **Disclaimer & Copyright
Notice** in the [README](README.md) - if a rights holder asks for removal, the
project complies.
