using System.Buffers.Binary;
using System.Text;
using DcAnim.Core.Atlas;

namespace DcAnim.Tests;

public sealed class BatlParserTests
{
    private static byte[] BuildBatl(params (string Page, (string Name, int X, int Y, int W, int H, int Ox, int Oy, int Ow, int Oh)[] Regions)[] pages)
    {
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("BATL"));
        Span<byte> tmp = stackalloc byte[2];
        bool first = true;
        foreach (var (page, regions) in pages)
        {
            if (!first)
                buf.Add(0);
            first = false;
            var pn = Encoding.UTF8.GetBytes(page);
            buf.Add((byte)pn.Length);
            buf.AddRange(pn);
            foreach (var r in regions)
            {
                var rn = Encoding.UTF8.GetBytes(r.Name);
                buf.Add((byte)rn.Length);
                buf.AddRange(rn);
                buf.AddRange(new byte[] { 0, 0 });
                foreach (int v in new[] { r.X, r.Y, r.W, r.H, r.Ox, r.Oy, r.Ow, r.Oh })
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(tmp, (ushort)v);
                    buf.Add(tmp[0]);
                    buf.Add(tmp[1]);
                }
            }
        }
        return buf.ToArray();
    }

    [Fact]
    public void Parse_SinglePageTwoRegions_ReadsAllFields()
    {
        var data = BuildBatl(("sheet0.png", new[]
        {
            ("run_00", 10, 20, 30, 40, 5, 6, 100, 100),
            ("run_01", 50, 20, 30, 40, 5, 6, 100, 100),
        }));

        var atlas = BatlParser.Parse(data);

        Assert.Equal(new[] { "sheet0.png" }, atlas.Pages);
        Assert.Equal(2, atlas.Regions.Count);
        Assert.Equal(0, atlas.DroppedTrailingRegions);
        var r = atlas.Regions[0];
        Assert.Equal("run_00", r.Name);
        Assert.Equal(0, r.Page);
        Assert.Equal((10, 20, 30, 40, 5, 6, 100, 100),
            (r.X, r.Y, r.Width, r.Height, r.OffsetX, r.OffsetY, r.OriginalWidth, r.OriginalHeight));
    }

    [Fact]
    public void Parse_MultiPage_AssignsPageIndexes()
    {
        var data = BuildBatl(
            ("a.png", new[] { ("idle_00", 0, 0, 10, 10, 0, 0, 32, 32) }),
            ("b.png", new[] { ("idle_01", 5, 5, 10, 10, 1, 1, 32, 32) }));

        var atlas = BatlParser.Parse(data);

        Assert.Equal(new[] { "a.png", "b.png" }, atlas.Pages);
        Assert.Equal(0, atlas.Regions[0].Page);
        Assert.Equal(1, atlas.Regions[1].Page);
    }

    [Fact]
    public void Parse_BadMagic_Throws() =>
        Assert.Throws<InvalidDataException>(() => BatlParser.Parse("NOPE"u8.ToArray()));

    [Fact]
    public void Parse_Empty_Throws() =>
        Assert.Throws<InvalidDataException>(() => BatlParser.Parse(Array.Empty<byte>()));

    [Fact]
    public void Parse_TruncatedTrailingRegion_DropsAndCounts()
    {
        var full = BuildBatl(("a.png", new[]
        {
            ("ok_00", 0, 0, 10, 10, 0, 0, 32, 32),
            ("cut_01", 1, 1, 10, 10, 0, 0, 32, 32),
        }));
        var cut = full[..^2]; // like the shipped beheaded.atlas tail

        var atlas = BatlParser.Parse(cut);

        Assert.Single(atlas.Regions);
        Assert.Equal(1, atlas.DroppedTrailingRegions);
    }
}
