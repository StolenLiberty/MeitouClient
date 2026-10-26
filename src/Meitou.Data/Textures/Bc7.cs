using System.Buffers.Binary;
using System.Numerics;

namespace Meitou.Data.Textures;

/// <summary>
/// BC7 (BPTC) block decoding to RGBA8, from the public format description (Microsoft, "BC7 Format" and the Direct3D 11 functional specification; Khronos Data Format Specification,
/// "BPTC"). Layout facts are in docs/formats/dds.md. A block is 16 bytes, read as a 128-bit little-endian number from bit 0: the mode (a unary prefix: mode m is m zero bits and a one),
/// then the partition, rotation and index-selection fields, the endpoints channel by channel (red of every endpoint, then green, blue, alpha), the p-bits, the primary indices and the
/// secondary indices. A block of all-zero mode bits is reserved and decodes to transparent black.
/// </summary>
public static class Bc7
{
    /// <summary>One mode's fields: subsets, partition / rotation / index-selection bits, colour and alpha endpoint bits, a p-bit per endpoint or per subset, primary and secondary index bits.</summary>
    readonly record struct Mode(int Subsets, int PartitionBits, int RotationBits, int SelectionBits, int ColourBits, int AlphaBits, bool EndpointP, bool SharedP, int IndexBits, int Index2Bits);

    static readonly Mode[] Modes =
    [
        new(3, 4, 0, 0, 4, 0, true, false, 3, 0),
        new(2, 6, 0, 0, 6, 0, false, true, 3, 0),
        new(3, 6, 0, 0, 5, 0, false, false, 2, 0),
        new(2, 6, 0, 0, 7, 0, true, false, 2, 0),
        new(1, 0, 2, 1, 5, 6, false, false, 2, 3),
        new(1, 0, 2, 0, 7, 8, false, false, 2, 2),
        new(1, 0, 0, 0, 7, 7, true, false, 4, 0),
        new(2, 6, 0, 0, 5, 5, true, false, 2, 0),
    ];

    /// <summary>The two-subset partitions: bit i is pixel i's subset.</summary>
    static readonly ushort[] Partitions2 =
    [
        0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80, 0xc800, 0xffec, 0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000,
        0xf710, 0x008e, 0x7100, 0x08ce, 0x008c, 0x7310, 0x3100, 0x8cce, 0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0, 0x718e, 0x399c,
        0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a, 0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660,
        0x0272, 0x04e4, 0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c, 0x9336, 0x9cc6, 0x817e, 0xe718, 0xccf0, 0x0fcc, 0x7744, 0xee22,
    ];

    /// <summary>The three-subset partitions: bits 2i..2i+1 are pixel i's subset.</summary>
    static readonly uint[] Partitions3 =
    [
        0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0, 0x5a5a5050,
        0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4, 0xa9a59450, 0x2a0a4250,
        0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454, 0x6a6a4040, 0xa4a45000, 0x1a1a0500,
        0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400, 0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200,
        0xa9a58000, 0x5090a0a8, 0xa8a09050, 0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50,
        0x500aa550, 0xaaaa4444, 0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600,
        0xaa444444, 0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000,
        0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44, 0x2a4a5254,
    ];

    /// <summary>The anchor pixel of subset 1 in a two-subset partition (subset 0's is pixel 0).</summary>
    static readonly byte[] Anchor2 =
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];

    /// <summary>The anchor pixels of subsets 1 and 2 in a three-subset partition.</summary>
    static readonly byte[] Anchor3A =
    [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    ];

    static readonly byte[] Anchor3B =
    [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    ];

    static readonly byte[] Weights2 = [0, 21, 43, 64];
    static readonly byte[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
    static readonly byte[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    static byte[] WeightsOf(int bits) => bits switch { 2 => Weights2, 3 => Weights3, _ => Weights4 };

    /// <summary>Reads fields from the 128 bits, lowest first.</summary>
    ref struct Bits(ulong lo, ulong hi)
    {
        int pos;

        public int Read(int n)
        {
            if (n == 0) return 0;
            ulong v = pos >= 64 ? hi >> (pos - 64) : pos + n <= 64 ? lo >> pos : (lo >> pos) | (hi << (64 - pos));
            pos += n;
            return (int)(v & ((1UL << n) - 1));
        }
    }

    /// <summary>Decodes one 16-byte block into <paramref name="block"/> (16 pixels, RGBA8, rows of 4).</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> src, Span<byte> block)
    {
        if (src[0] == 0)
        {
            block[..64].Clear();
            return;
        }
        int modeIndex = BitOperations.TrailingZeroCount(src[0]);
        var mode = Modes[modeIndex];
        var bits = new Bits(BinaryPrimitives.ReadUInt64LittleEndian(src), BinaryPrimitives.ReadUInt64LittleEndian(src[8..]));
        bits.Read(modeIndex + 1);
        int partition = bits.Read(mode.PartitionBits);
        int rotation = bits.Read(mode.RotationBits);
        int selection = bits.Read(mode.SelectionBits);

        int ends = mode.Subsets * 2;
        Span<int> e = stackalloc int[6 * 4];   // endpoint i, channel c at i * 4 + c
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < ends; i++) e[i * 4 + c] = bits.Read(mode.ColourBits);
        if (mode.AlphaBits > 0)
            for (int i = 0; i < ends; i++) e[i * 4 + 3] = bits.Read(mode.AlphaBits);

        int colourBits = mode.ColourBits, alphaBits = mode.AlphaBits;
        if (mode.EndpointP || mode.SharedP)
        {
            Span<int> p = stackalloc int[6];
            if (mode.EndpointP) for (int i = 0; i < ends; i++) p[i] = bits.Read(1);
            else for (int s = 0; s < mode.Subsets; s++) p[2 * s] = p[2 * s + 1] = bits.Read(1);
            for (int i = 0; i < ends; i++)
            {
                for (int c = 0; c < 3; c++) e[i * 4 + c] = e[i * 4 + c] << 1 | p[i];
                if (alphaBits > 0) e[i * 4 + 3] = e[i * 4 + 3] << 1 | p[i];
            }
            colourBits++;
            if (alphaBits > 0) alphaBits++;
        }
        for (int i = 0; i < ends; i++)
        {
            for (int c = 0; c < 3; c++) e[i * 4 + c] = Expand(e[i * 4 + c], colourBits);
            e[i * 4 + 3] = alphaBits > 0 ? Expand(e[i * 4 + 3], alphaBits) : 255;
        }

        Span<int> subset = stackalloc int[16];
        for (int i = 0; i < 16; i++)
            subset[i] = mode.Subsets switch
            {
                2 => (Partitions2[partition] >> i) & 1,
                3 => (int)(Partitions3[partition] >> (2 * i)) & 3,
                _ => 0,
            };
        // Each subset's anchor pixel stores its index with one bit less (the top bit is 0): subset 0's is pixel 0, the others' come from the anchor tables.
        int anchor1 = mode.Subsets == 2 ? Anchor2[partition] : mode.Subsets == 3 ? Anchor3A[partition] : -1;
        int anchor2 = mode.Subsets == 3 ? Anchor3B[partition] : -1;

        Span<int> primary = stackalloc int[16];
        Span<int> secondary = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            bool anchor = i == 0 || i == anchor1 && subset[i] == 1 || i == anchor2 && subset[i] == 2;
            primary[i] = bits.Read(anchor ? mode.IndexBits - 1 : mode.IndexBits);
        }
        if (mode.Index2Bits > 0)
            for (int i = 0; i < 16; i++) secondary[i] = bits.Read(i == 0 ? mode.Index2Bits - 1 : mode.Index2Bits);

        // Colour and alpha weights: with a second index set, the index-selection bit says which set the colour takes (the other is alpha's).
        bool swap = mode.Index2Bits > 0 && selection == 1;
        var colourWeights = WeightsOf(swap ? mode.Index2Bits : mode.IndexBits);
        var alphaWeights = WeightsOf(mode.Index2Bits > 0 && !swap ? mode.Index2Bits : mode.IndexBits);
        for (int i = 0; i < 16; i++)
        {
            int e0 = subset[i] * 2 * 4, e1 = e0 + 4;
            int ci = mode.Index2Bits > 0 && swap ? secondary[i] : primary[i];
            int ai = mode.Index2Bits > 0 && !swap ? secondary[i] : primary[i];
            int cw = colourWeights[ci], aw = alphaWeights[ai];
            int r = Lerp(e[e0], e[e1], cw), g = Lerp(e[e0 + 1], e[e1 + 1], cw), b = Lerp(e[e0 + 2], e[e1 + 2], cw), a = Lerp(e[e0 + 3], e[e1 + 3], aw);
            switch (rotation)
            {
                case 1: (r, a) = (a, r); break;
                case 2: (g, a) = (a, g); break;
                case 3: (b, a) = (a, b); break;
            }
            block[i * 4] = (byte)r;
            block[i * 4 + 1] = (byte)g;
            block[i * 4 + 2] = (byte)b;
            block[i * 4 + 3] = (byte)a;
        }
    }

    /// <summary>An n-bit endpoint widened to 8 bits: shifted to the top, its high bits repeated below.</summary>
    static int Expand(int v, int n)
    {
        v <<= 8 - n;
        return v | v >> n;
    }

    static int Lerp(int e0, int e1, int w) => ((64 - w) * e0 + w * e1 + 32) >> 6;
}
