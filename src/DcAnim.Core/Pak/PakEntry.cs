namespace DcAnim.Core.Pak;

/// <summary>
/// A single file entry inside a Dead Cells PAK archive.
/// Offsets are relative to the start of the data section (end of the file table).
/// </summary>
public sealed record PakEntry(string Path, uint Offset, uint Size, uint Crc);
