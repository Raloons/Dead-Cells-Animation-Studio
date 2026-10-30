using System.Text;

namespace DcAnim.Core.Pak;

/// <summary>
/// Reads Dead Cells PAK v1 archives (stock <c>res.pak</c>: magic <c>PAK\x01</c>).
/// Layout (little-endian, verified against stock res.pak 2026-10-28):
/// <list type="bullet">
/// <item><c>PAK\x01</c>, u32 data-section offset, NUL-terminated hash string, u8 version, u32 root entry count.</item>
/// <item>Recursive entries: u8 name length, UTF-8 name, u8 type (0 = file, 1 = directory).
/// File: u32 offset, u32 size, u32 Adler-32. Directory: u32 child count, then children.</item>
/// <item>Data section: complete raw payloads; no per-file prefix.</item>
/// </list>
/// The reader never writes to the archive. It only parses the table and streams payloads.
/// </summary>
public sealed class PakReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _disposed;

    /// <summary>Archive-relative paths using '/' separators, e.g. <c>atlas/beheaded.atlas</c>.</summary>
    public IReadOnlyDictionary<string, PakEntry> Entries { get; }

    /// <summary>Absolute offset in the archive where the data section begins.</summary>
    public long DataStart { get; }

    private PakReader(Stream stream, bool leaveOpen, IReadOnlyDictionary<string, PakEntry> entries, long dataStart)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Entries = entries;
        DataStart = dataStart;
    }

    public static PakReader Open(string pakPath)
    {
        var stream = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return Open(stream, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static PakReader Open(Stream stream, bool leaveOpen = true)
    {
        if (!stream.CanSeek)
            throw new NotSupportedException("PakReader requires a seekable stream.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        long start = stream.Position;

        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'P' || magic[1] != (byte)'A' || magic[2] != (byte)'K')
            throw new InvalidDataException("Not a Dead Cells PAK archive (bad magic).");
        if (magic[3] != 0x01)
            throw new NotSupportedException($"Unsupported PAK version byte 0x{magic[3]:X2}; only stock PAK\\x01 is supported.");

        uint dataOffset = reader.ReadUInt32();

        // NUL-terminated hash/identifier string.
        var hashBytes = new List<byte>();
        byte b;
        while ((b = reader.ReadByte()) != 0)
            hashBytes.Add(b);

        _ = reader.ReadByte(); // version (observed 0x01)
        uint rootCount = reader.ReadUInt32();

        var entries = new Dictionary<string, PakEntry>(StringComparer.Ordinal);
        var stack = new Stack<(uint Count, string Prefix)>();
        stack.Push((rootCount, string.Empty));

        string ReadName()
        {
            int len = reader.ReadByte();
            var bytes = reader.ReadBytes(len);
            if (bytes.Length != len)
                throw new EndOfStreamException("Unexpected end of PAK file table.");
            return Encoding.UTF8.GetString(bytes);
        }

        while (stack.Count > 0)
        {
            var (count, prefix) = stack.Pop();
            if (count == 0) continue;
            // The serialized table is depth-first: directory children precede
            // the next sibling. Save siblings, then process children immediately.
            stack.Push((count - 1, prefix));
            {
                string name = ReadName();
                string full = string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
                int type = reader.ReadByte();
                if (type == 1)
                {
                    uint children = reader.ReadUInt32();
                    stack.Push((children, full));
                }
                else if (type == 0)
                {
                    uint offset = reader.ReadUInt32();
                    uint size = reader.ReadUInt32();
                    uint crc = reader.ReadUInt32();
                    if (!entries.TryAdd(full, new PakEntry(full, offset, size, crc)))
                        throw new InvalidDataException($"Duplicate PAK path '{full}'.");
                }
                else
                {
                    throw new InvalidDataException($"Unknown PAK entry type {type} for '{full}'.");
                }
            }
        }

        long dataStart = checked(start + dataOffset);
        if (dataStart < stream.Position || dataStart > stream.Length)
            throw new InvalidDataException("PAK data offset overlaps its table or lies outside the archive.");
        return new PakReader(stream, leaveOpen, entries, dataStart);
    }

    public bool Contains(string archivePath) => Entries.ContainsKey(Normalize(archivePath));

    /// <summary>
    /// Reads the complete raw payload and verifies its Adler-32 checksum.
    /// </summary>
    public byte[] ReadFile(string archivePath)
    {
        if (!Entries.TryGetValue(Normalize(archivePath), out var entry))
            throw new FileNotFoundException($"Entry '{archivePath}' not found in PAK archive.");
        if (!_stream.CanSeek)
            throw new NotSupportedException("PakReader.ReadFile requires a seekable stream.");

        var buffer = new byte[entry.Size];
        lock (_stream)
        {
            if (DataStart + entry.Offset + entry.Size > _stream.Length)
                throw new EndOfStreamException($"Truncated payload for '{archivePath}'.");
            _stream.Position = DataStart + entry.Offset;
            int read = 0;
            while (read < buffer.Length)
            {
                int n = _stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    throw new EndOfStreamException($"Truncated payload for '{archivePath}'.");
                read += n;
            }
        }

        if (Adler32(buffer) != entry.Crc)
            throw new InvalidDataException($"PAK checksum mismatch for '{archivePath}'.");
        return buffer;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        // Bound each accumulation to avoid overflow while reducing modulo work.
        for (int start = 0; start < data.Length; start += 5552)
        {
            int end = Math.Min(data.Length, start + 5552);
            for (int i = start; i < end; i++) { a += data[i]; b += a; }
            a %= 65521;
            b %= 65521;
        }
        return b << 16 | a;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_leaveOpen)
            _stream.Dispose();
    }
}
