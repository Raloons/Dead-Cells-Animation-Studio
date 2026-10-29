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
            WriteUInt(table, (uint)(f.Body.Length + 4));
            WriteUInt(table, 0x12345678);
            payloads.AddRange(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }); // 4-byte header cookie
            payloads.AddRange(f.Body);
            offset += (uint)(f.Body.Length + 4);
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
        BinaryPrimitives.WriteUInt32LittleEndian(hint, (uint)table.Count);
        pak.AddRange(hint.ToArray());
        pak.AddRange(Encoding.ASCII.GetBytes("testhash"));
        pak.Add(0);
        pak.Add(1);
        pak.AddRange(table);
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
}
