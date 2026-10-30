namespace DcAnim.Core.Pak;

/// <summary>
/// A single file entry inside a Dead Cells PAK archive.
/// Offsets are relative to the header-declared data section. The historical
/// Crc property contains Adler-32, not CRC-32.
/// </summary>
public sealed record PakEntry(string Path, uint Offset, uint Size, uint Crc);
