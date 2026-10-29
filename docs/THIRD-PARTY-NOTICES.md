# Third-party notices

Dead Cells Animation Studio contains **no game assets**. At runtime it reads
the user's own legally obtained Dead Cells installation (`res.pak`,
read-only) or user-supplied extracted resources. No proprietary artwork,
audio, or configuration from the game or from third-party mods is bundled
in this repository or in application builds.

Motion Twin / Evil Empire official modding tools (`PAKTool`, `AtlasTool`,
`ModsDoc.pdf`) were used during research as documented; they are not
redistributed here.

## Direct dependencies (see `*.csproj` for pinned versions)

| Package | Purpose | License (upstream) |
|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent (11.3.0) | Desktop UI framework + Fluent theme | MIT — https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp (3.119.1) | PNG decode + frame composition in `DcAnim.Core` | MIT — https://github.com/mono/SkiaSharp |
| SixLabors.ImageSharp (3.1.8) | Animated GIF encode/decode in `DcAnim.Core` | Apache-2.0 — https://github.com/SixLabors/ImageSharp |
| xunit.v3 + xunit.runner.visualstudio + Microsoft.NET.Test.Sdk | Test framework (dev only) | Apache-2.0 — https://github.com/xunit/xunit |
| coverlet.collector | Code coverage (dev only) | MIT |

Avalonia pulls transitive native/runtime packages (e.g. SkiaSharp
native assets, HarfBuzzSharp, `Tmds.DBus.Protocol`); their licenses ship
with their packages on NuGet. Note: NuGet reports upstream advisories
(`NU1902`/`NU1903`) for `SixLabors.ImageSharp` 3.1.8 and `Tmds.DBus.Protocol`
0.21.2 (a Linux-only transitive dependency of Avalonia.Desktop); the former
is used only for local GIF encode/decode of user-generated exports, the
latter is not used on the Windows target.
