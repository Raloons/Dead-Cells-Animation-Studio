using System.Buffers.Binary;
using System.Text;
using DcAnim.Core.Pak;

namespace DcAnim.Tests;

public sealed class PakReaderTests
{
    private sealed record FileSpec(string Path, byte[] Body);

    private static byte[] BuildPak(params FileSpec[] files)
    {
        // Groups files by directory to emit the recursive table.
        var byDir = new Dictionary<string, List<(string Name, byte[] Body)>>();
        foreach (var f in files)
        {
            var parts = f.Path.Replace('\\', '/').Split('/');
            string dir = parts.Length > 1 ? string.Join('/', parts[..^1]) : string.Empty;
            if (!byDir.TryGetValue(dir, out var list))
                byDir[dir] = list = new List<(string, byte[])>();
            list.Add((parts[^1], f.Body));
        }

        var table = new List<byte>();
        void WriteName(string name)
        {
            var b = Encoding.UTF8.GetBytes(name);
            table.Add((byte)b.Length);
            table.AddRange(b);
        }

        var payloads = new List<byte>();
        uint offset = 0;
        var ordered = byDir.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

        // Root entry count = top-level dirs + root files.
        var topDirs = ordered.Where(k => k.Key != string.Empty).Select(k => k.Key.Split('/')[0]).Distinct().ToList();
        var rootFiles = byDir.TryGetValue(string.Empty, out var rf) ? rf : new List<(string Name, byte[] Body)>();
        WriteUInt(table, (uint)(topDirs.Count + rootFiles.Count));

        foreach (var f in rootFiles.OrderBy(x => x.Name))
            WriteFileEntry(f);

        foreach (var top in topDirs.OrderBy(x => x))
            WriteDir(top, top);

        void WriteDir(string prefix, string full)
        {
            WriteName(full.Split('/')[^1]);
            table.Add(1);
            var children = ordered.Where(k => k.Key == full).SelectMany(k => k.Value).ToList();
            var subDirs = ordered.Where(k => k.Key.StartsWith(full + "/", StringComparison.Ordinal))
                .Select(k => k.Key[full.Length..].Trim('/').Split('/')[0]).Distinct().ToList();
            WriteUInt(table, (uint)(children.Count + subDirs.Count));
            foreach (var f in children.OrderBy(x => x.Name))
                WriteFileEntry(f);
            foreach (var s in subDirs.OrderBy(x => x))
                WriteDir(s, full + "/" + s);
        }

        void WriteFileEntry((string Name, byte[] Body) f)
        {
            WriteName(f.Name);
            table.Add(0);
            WriteUInt(table, offset);
            WriteUInt(table, (uint)f.Body.Length);
            uint a = 1, b = 0;
            foreach (byte value in f.Body) { a = (a + value) % 65521; b = (b + a) % 65521; }
            WriteUInt(table, b << 16 | a);
            payloads.AddRange(f.Body);
            offset += (uint)f.Body.Length;
        }

        static void WriteUInt(List<byte> buf, uint v)
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, v);
            buf.AddRange(tmp.ToArray());
        }

        var pak = new List<byte>();
        pak.AddRange(Encoding.ASCII.GetBytes("PAK\x01"));
        Span<byte> hint = stackalloc byte[4];
        // Header offset includes header, directory table and four bytes of
        // table/data padding, matching the observed native resource layout.
        BinaryPrimitives.WriteUInt32LittleEndian(hint, (uint)(4 + 4 + 9 + 1 + table.Count + 4));
        pak.AddRange(hint.ToArray());
        pak.AddRange(Encoding.ASCII.GetBytes("testhash"));
        pak.Add(0);
        pak.Add(1);
        pak.AddRange(table);
        pak.AddRange(new byte[4]);
        pak.AddRange(payloads);
        return pak.ToArray();
    }

    [Fact]
    public void Open_ListsEntriesAndReadsPayloads()
    {
        var bytes = BuildPak(
            new FileSpec("atlas/beheaded.atlas", "BATL-data"u8.ToArray()),
            new FileSpec("atlas/beheaded0.png", new byte[] { 1, 2, 3 }));

        using var reader = PakReader.Open(new MemoryStream(bytes));

        Assert.True(reader.Contains("atlas/beheaded.atlas"));
        Assert.True(reader.Contains("atlas/beheaded0.png"));
        Assert.False(reader.Contains("atlas/missing.png"));
        Assert.Equal("BATL-data"u8.ToArray(), reader.ReadFile("atlas/beheaded.atlas"));
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadFile("atlas/beheaded0.png"));
    }

    [Fact]
    public void ReadFile_MissingEntry_Throws() =>
        Assert.Throws<FileNotFoundException>(() =>
        {
            using var reader = PakReader.Open(new MemoryStream(BuildPak()));
            reader.ReadFile("nope.png");
        });

    [Fact]
    public void Open_BadMagic_Throws() =>
        Assert.Throws<InvalidDataException>(() => PakReader.Open(new MemoryStream("XXXX...."u8.ToArray())));

    [Fact]
    public void Open_UnsupportedVersion_Throws()
    {
        var bytes = BuildPak();
        bytes[3] = 0x00; // mod-style magic byte
        Assert.Throws<NotSupportedException>(() => PakReader.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void Open_NestedDirectoriesAndSiblings_RetainsDistinctBasenames()
    {
        var files = new[]
        {
            new FileSpec("data.cdb", "root-tail"u8.ToArray()),
            new FileSpec("atlas/hero/page.png", "hero-tail"u8.ToArray()),
            new FileSpec("atlas/page.png", "atlas-tail"u8.ToArray()),
            new FileSpec("sound/nested/page.png", "sound-tail"u8.ToArray()),
            new FileSpec("sound/z.wav", new byte[] { 7 })
        };
        using var reader = PakReader.Open(new MemoryStream(BuildPak(files)));
        Assert.Equal(files.Length, reader.Entries.Count);
        foreach (var file in files) Assert.Equal(file.Body, reader.ReadFile(file.Path));
    }

    [Fact]
    public void ReadFile_PreservesShortEmptyAndFinalFourBytes()
    {
        var bytes = BuildPak(new("empty", []), new("short", [1, 2, 3]),
            new("json", "{\"tail\":true}"u8.ToArray()));
        using var reader = PakReader.Open(new MemoryStream(bytes));
        Assert.Empty(reader.ReadFile("empty"));
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadFile("short"));
        Assert.Equal("{\"tail\":true}"u8.ToArray(), reader.ReadFile("json"));
    }

    [Fact]
    public void ReadFile_CorruptedPayload_RejectsAdlerMismatch()
    {
        var bytes = BuildPak(new FileSpec("hello", "hello"u8.ToArray()));
        bytes[^1] ^= 1;
        using var reader = PakReader.Open(new MemoryStream(bytes));
        Assert.Throws<InvalidDataException>(() => reader.ReadFile("hello"));
    }

    [Fact]
    public void Open_EmbeddedArchive_UsesArchiveRelativeDataOffset()
    {
        var bytes = new byte[11].Concat(BuildPak(new FileSpec("file", [9, 8, 7, 6, 5]))).ToArray();
        using var stream = new MemoryStream(bytes) { Position = 11 };
        using var reader = PakReader.Open(stream);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, reader.ReadFile("file"));
    }

    [Fact]
    public void Open_DataOffsetInsideTable_Rejects()
    {
        var bytes = BuildPak(new FileSpec("file", [1]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 1);
        Assert.Throws<InvalidDataException>(() => PakReader.Open(new MemoryStream(bytes)));
    }
}
