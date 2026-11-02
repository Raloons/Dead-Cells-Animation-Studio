# Native resource decoder correction - 2026-10-30

During weapon-substitution research, independent archive reads found three
coupled PAK-reader errors:

1. The u32 at byte four is the data-section offset, not a table-size hint.
   Stock v35 declares **238,570**; the parsed table ends at **238,566**.
2. Reading four bytes early and stripping a supposed cookie recovered the
   resource start but lost its final four bytes. Resources have no such cookie.
3. Directory children must be consumed immediately, before the next sibling.
   Deferring them misassigned paths and collapsed duplicate basenames.

`PakReader` now honors the header, traverses depth-first, retains complete raw
payloads, rejects duplicate paths and verifies the stored **Adler-32** field.
The historical `PakEntry.Crc` property name remains API-compatible.
`BatlParser` also recognizes the terminal empty page name.

Read-only validation target: locally owned stock v35 `res.pak`, SHA-256
`cb8d2b8a3604f34342c432c8f0bfe934f9a8859b49fea2c96db4dbca4495a96d`.

Expected corrected observations:
- **7,055** file entries, including root `data.cdb`.
- **10,234** complete base-hero regions and **three** pages.
- Complete `scissorsAtkC_04`, **zero** dropped trailing regions.
- Valid complete `beheaded_tracks.json` with **572** animation groups.
- Complete PNG IEND tail, native payload checksums passing.

Synthetic regression cases cover sibling/nested directories, duplicate
basenames in distinct folders, explicit data padding, archive-relative offsets
at a nonzero stream origin, empty/short payloads, tail preservation, corrupted
checksums and invalid data offsets. Native integration checks exercise the
actual local archive without changing it. No proprietary assets are included.

This corrects earlier M1 documentation that attributed the missing tail to
shipped data. It is a resource-decoder change; existing workspace edits remain
separate in the working tree.

Verification: `dotnet test tests/DcAnim.Tests/DcAnim.Tests.csproj -c Release`
passed **177**, skipped **1** (optional external AtlasTool reference). The native
archive assertions above passed against the local installation. App Release
build passed with **0 errors**, 30 existing dependency/deprecation warnings.
No running editor was terminated or restarted.
