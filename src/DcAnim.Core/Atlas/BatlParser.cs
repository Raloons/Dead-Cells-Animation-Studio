using System.Buffers.Binary;
using System.Text;

namespace DcAnim.Core.Atlas;

/// <summary>
/// Parses Dead Cells binary BATL atlases (magic <c>BATL</c>).
/// Grammar (reverse-engineered 2026-10-28, validated pixel-exact against
/// official AtlasTool -Expand output for <c>atlas/beheaded.atlas</c>):
/// <code>
/// 'BATL' { page } EOF
/// page   := u8 pageNameLen, pageName, { region }, 0x00
/// region := u8 regionNameLen, regionName, u16 flags,
///           u16 x, y, w, h, offsetX, offsetY, origW, origH
/// </code>
/// The next page follows its 0x00 terminator as u8 name length + name.
/// Offsets paste top-left onto the original-size canvas.
/// A terminal empty page name ends the atlas. Incomplete trailing regions in
/// damaged inputs are counted in <see cref="BatlAtlasData.DroppedTrailingRegions"/>.
/// </summary>
public static class BatlParser
{
    public static BatlAtlasData Parse(byte[] data)
    {
        if (data.Length < 4 || data[0] != (byte)'B' || data[1] != (byte)'A' || data[2] != (byte)'T' || data[3] != (byte)'L')
            throw new InvalidDataException("Not a BATL atlas (bad magic).");

        var pages = new List<string>();
        var regions = new List<BatlRegion>();
        int dropped = 0;
        int pos = 4;
        int currentPage = -1;

        string ReadString(int length)
        {
            if (pos + length > data.Length)
                throw new EndOfStreamException("Truncated BATL string.");
            string s = Encoding.UTF8.GetString(data, pos, length);
            pos += length;
            return s;
        }

        ushort ReadU16()
        {
            if (pos + 2 > data.Length)
                throw new EndOfStreamException("Truncated BATL data.");
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
            return v;
        }

        bool ExpectPage()
        {
            if (pos >= data.Length)
                return false;
            int nameLen = data[pos++];
            if (nameLen == 0)
                return false;
            if (pos + nameLen > data.Length)
                throw new EndOfStreamException("Truncated BATL page name.");
            pages.Add(ReadString(nameLen));
            currentPage = pages.Count - 1;
            return true;
        }

        if (!ExpectPage())
            throw new InvalidDataException("BATL atlas has no pages.");

        while (pos < data.Length)
        {
            int nameLen = data[pos++];
            if (nameLen == 0)
            {
                if (!ExpectPage())
                    break;
                continue;
            }

            // A region needs name + flags(2) + 8xu16(16) bytes. Preserve the
            // explicit damaged-input diagnostic without inventing a region.
            if (pos + nameLen + 18 > data.Length)
            {
                dropped++;
                break;
            }

            string name = ReadString(nameLen);
            _ = ReadU16(); // flags (observed 0)
            int x = ReadU16();
            int y = ReadU16();
            int w = ReadU16();
            int h = ReadU16();
            int ox = ReadU16();
            int oy = ReadU16();
            int ow = ReadU16();
            int oh = ReadU16();

            regions.Add(new BatlRegion(name, currentPage, x, y, w, h, ox, oy, ow, oh));
        }

        return new BatlAtlasData(pages, regions, dropped);
    }

    public static BatlAtlasData ParseFile(string path) => Parse(File.ReadAllBytes(path));
}
