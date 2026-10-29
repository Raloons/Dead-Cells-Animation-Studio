using System.Text;

namespace DcAnim.Core.Pak;

/// <summary>
/// Reads Dead Cells PAK v1 archives (stock <c>res.pak</c>: magic <c>PAK\x01</c>).
/// Layout (little-endian, verified against stock res.pak 2026-10-28):
/// <list type="bullet">
/// <item><c>PAK\x01</c>, u32 table-size hint, NUL-terminated hash string, u8 version, u32 root entry count.</item>
/// <item>Recursive entries: u8 name length, UTF-8 name, u8 type (0 = file, 1 = directory).
/// File: u32 offset, u32 size, u32 crc. Directory: u32 child count, then children.</item>
/// <item>Data section: each payload prefixed by a 4-byte header (cookie), stripped on read.</item>
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
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        long start = stream.CanSeek ? stream.Position : 0;

        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'P' || magic[1] != (byte)'A' || magic[2] != (byte)'K')
            throw new InvalidDataException("Not a Dead Cells PAK archive (bad magic).");
        if (magic[3] != 0x01)
            throw new NotSupportedException($"Unsupported PAK version byte 0x{magic[3]:X2}; only stock PAK\\x01 is supported.");

        _ = reader.ReadUInt32(); // table-size hint; data start is derived from actual parse position.

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
            for (uint i = 0; i < count; i++)
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
                    entries[full] = new PakEntry(full, offset, size, crc);
                }
                else
                {
                    throw new InvalidDataException($"Unknown PAK entry type {type} for '{full}'.");
                }
            }
        }

        long dataStart = (stream.CanSeek ? stream.Position : start);
        return new PakReader(stream, leaveOpen, entries, dataStart);
    }

    public bool Contains(string archivePath) => Entries.ContainsKey(Normalize(archivePath));

    /// <summary>
    /// Reads a payload and strips the 4-byte per-file header cookie,
    /// returning the raw content (e.g. starting with <c>PNG</c>, <c>BATL</c> or <c>{</c>).
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

        if (buffer.Length < 4)
            throw new InvalidDataException($"Payload for '{archivePath}' is shorter than the 4-byte header.");
        var body = new byte[buffer.Length - 4];
        Buffer.BlockCopy(buffer, 4, body, 0, body.Length);
        return body;
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
