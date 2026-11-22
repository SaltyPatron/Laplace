using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Native;

namespace Laplace.Engine.Core;

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
public readonly record struct Hash128(ulong Hi, ulong Lo)
{
    public static readonly Hash128 Zero = new(0, 0);

    /// <summary>Canonical Native UAX #29 tree identity over <c>tier0.bin</c> via <c>lp_text_id</c>.</summary>
    public static Hash128 Text(string text) => NativeTextIdentity.OfText(text);

    /// <summary>Tier-0 single codepoint identity via <c>lp_id_codepoint</c>.</summary>
    public static unsafe Hash128 Codepoint(uint codepoint)
    {
        lp_id id;
        LaplaceNative.lp_id_codepoint(codepoint, &id);
        return FromNative(id);
    }

    /// <summary>Flat codepoints composition identity for UTF-8 bytes via <c>lp_id_codepoints_utf8</c>.</summary>
    public static unsafe bool TryCodepointsUtf8(ReadOnlySpan<byte> utf8, out Hash128 id)
    {
        if (utf8.IsEmpty)
        {
            id = Zero;
            return false;
        }
        fixed (byte* p = utf8)
        {
            lp_id native;
            if (!LaplaceNative.lp_id_codepoints_utf8(p, (nuint)utf8.Length, &native))
            {
                id = Zero;
                return false;
            }
            id = FromNative(native);
            return true;
        }
    }

    /// <summary>Universal Merkle child-sequence identity via <c>lp_id_compose</c> (n==1 collapses to child[0]).</summary>
    public static unsafe Hash128 Merkle(byte tier, ReadOnlySpan<Hash128> children)
    {
        _ = tier; // Tier is never part of canonical content identity.
        if (children.IsEmpty)
            return Blake3(ReadOnlySpan<byte>.Empty);
        if (children.Length == 1)
            return children[0];

        lp_id result;
        fixed (Hash128* p = children)
        {
            LaplaceNative.lp_id_compose((lp_id*)p, (nuint)children.Length, &result);
        }
        return FromNative(result);
    }

    public unsafe lp_id ToNative()
    {
        lp_id id = default;
        WriteBytes(new Span<byte>(id.b, 16));
        return id;
    }

    public static unsafe Hash128 FromNative(in lp_id id)
    {
        lp_id copy = id;
        return FromBytes(new ReadOnlySpan<byte>(copy.b, 16));
    }

    /// <summary>
    /// BLAKE3-128 cryptographic digest for arbitrary byte payloads (cartridges, fingerprints, raw blobs).
    /// Text and Merkle composition MUST use <see cref="Text"/>, <see cref="Codepoint"/>, or <see cref="Merkle"/>.
    /// </summary>
    public static Hash128 Blake3(ReadOnlySpan<byte> data)
    {
        Span<byte> digest = stackalloc byte[16];
        Blake3Digest128(data, digest);
        return FromBytes(digest);
    }

    /// <summary>
    /// memcmp of the 16-byte host layout — same order as <c>lp_id_cmp</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareToBytewise(Hash128 other)
    {
        int c = BinaryPrimitives.ReverseEndianness(Hi)
            .CompareTo(BinaryPrimitives.ReverseEndianness(other.Hi));
        if (c != 0) return c;
        return BinaryPrimitives.ReverseEndianness(Lo)
            .CompareTo(BinaryPrimitives.ReverseEndianness(other.Lo));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EqualsBytewise(Hash128 other) => Hi == other.Hi && Lo == other.Lo;

    public byte[] ToBytes()
    {
        var b = new byte[16];
        WriteBytes(b);
        return b;
    }

    public static Hash128 FromBytes(ReadOnlySpan<byte> src)
    {
        if (src.Length < 16) throw new ArgumentException("src must hold 16 bytes", nameof(src));
        return MemoryMarshal.Read<Hash128>(src);
    }

    public static bool TryParseHex(string? hex, out Hash128 id)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 32)
        {
            id = Zero;
            return false;
        }
        Span<byte> bytes = stackalloc byte[16];
        if (Convert.FromHexString(hex, bytes, out _, out _) != System.Buffers.OperationStatus.Done)
        {
            id = Zero;
            return false;
        }
        id = FromBytes(bytes);
        return true;
    }

    public static Hash128 ParseHex(string hex) =>
        TryParseHex(hex, out var id)
            ? id
            : throw new FormatException($"Expected 32-character hexadecimal BLAKE3-128 id, got '{hex}'.");

    public void WriteBytes(Span<byte> dest)
    {
        if (dest.Length < 16) throw new ArgumentException("dest must hold 16 bytes", nameof(dest));
        unsafe
        {
            Hash128 self = this;
            new ReadOnlySpan<byte>(&self, 16).CopyTo(dest);
        }
    }

    public string ToHex()
    {
        Span<byte> b = stackalloc byte[16];
        WriteBytes(b);
        return Convert.ToHexStringLower(b);
    }

    // ---- Standard BLAKE3-128 (single-pass arbitrary-byte cryptographic digest) ----
    private const int BlockLen = 64;
    private const int ChunkLen = 1024;
    private const uint ChunkStart = 1u << 0;
    private const uint ChunkEnd = 1u << 1;
    private const uint ParentFlag = 1u << 2;
    private const uint RootFlag = 1u << 3;

    private static ReadOnlySpan<uint> Iv =>
    [
        0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au,
        0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u,
    ];

    private static void G(Span<uint> s, int a, int b, int c, int d, uint mx, uint my)
    {
        s[a] = s[a] + s[b] + mx;
        s[d] = BitOperations.RotateRight(s[d] ^ s[a], 16);
        s[c] += s[d];
        s[b] = BitOperations.RotateRight(s[b] ^ s[c], 12);
        s[a] = s[a] + s[b] + my;
        s[d] = BitOperations.RotateRight(s[d] ^ s[a], 8);
        s[c] += s[d];
        s[b] = BitOperations.RotateRight(s[b] ^ s[c], 7);
    }

    private static void Round(Span<uint> s, ReadOnlySpan<uint> m)
    {
        G(s, 0, 4, 8, 12, m[0], m[1]);
        G(s, 1, 5, 9, 13, m[2], m[3]);
        G(s, 2, 6, 10, 14, m[4], m[5]);
        G(s, 3, 7, 11, 15, m[6], m[7]);
        G(s, 0, 5, 10, 15, m[8], m[9]);
        G(s, 1, 6, 11, 12, m[10], m[11]);
        G(s, 2, 7, 8, 13, m[12], m[13]);
        G(s, 3, 4, 9, 14, m[14], m[15]);
    }

    private static void Permute(Span<uint> m)
    {
        Span<uint> t = stackalloc uint[16];
        ReadOnlySpan<byte> p = [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8];
        for (int i = 0; i < 16; i++) t[i] = m[p[i]];
        t.CopyTo(m);
    }

    private static void Compress(
        ReadOnlySpan<uint> cv, ReadOnlySpan<byte> block, int blockLen,
        ulong counter, uint flags, Span<uint> out16)
    {
        Span<uint> m = stackalloc uint[16];
        m.Clear();
        Span<byte> padded = stackalloc byte[BlockLen];
        padded.Clear();
        block[..blockLen].CopyTo(padded);
        for (int i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.Slice(i * 4, 4));

        Span<uint> s = stackalloc uint[16];
        for (int i = 0; i < 8; i++) s[i] = cv[i];
        s[8] = Iv[0]; s[9] = Iv[1]; s[10] = Iv[2]; s[11] = Iv[3];
        s[12] = (uint)counter;
        s[13] = (uint)(counter >> 32);
        s[14] = (uint)blockLen;
        s[15] = flags;

        for (int r = 0; r < 7; r++)
        {
            Round(s, m);
            if (r < 6) Permute(m);
        }

        for (int i = 0; i < 8; i++)
        {
            out16[i] = s[i] ^ s[i + 8];
            out16[i + 8] = s[i + 8] ^ cv[i];
        }
    }

    private static void Blake3Digest128(ReadOnlySpan<byte> input, Span<byte> out16Bytes)
    {
        Span<uint> cvStack = stackalloc uint[54 * 8];
        int cvCount = 0;
        Span<uint> chunkCv = stackalloc uint[8];
        Span<uint> blockOut = stackalloc uint[16];
        Span<uint> rootCv = stackalloc uint[8];
        Span<byte> rootBlock = stackalloc byte[BlockLen];

        if (input.Length <= ChunkLen)
        {
            Iv.CopyTo(chunkCv);
            int offset = 0;
            int rem = input.Length;
            int blocks = rem == 0 ? 1 : (rem + BlockLen - 1) / BlockLen;
            for (int b = 0; b < blocks; b++)
            {
                int take = Math.Min(BlockLen, rem - offset);
                uint flags = (b == 0 ? ChunkStart : 0u) | (b == blocks - 1 ? ChunkEnd | RootFlag : 0u);
                if (b == blocks - 1)
                {
                    Compress(chunkCv, input.Slice(offset, take), take, 0, flags, blockOut);
                    for (int i = 0; i < 4; i++)
                        BinaryPrimitives.WriteUInt32LittleEndian(out16Bytes.Slice(i * 4, 4), blockOut[i]);
                    return;
                }
                Compress(chunkCv, input.Slice(offset, take), take, 0, flags, blockOut);
                blockOut[..8].CopyTo(chunkCv);
                offset += take;
            }
            return;
        }

        ulong chunkIdx = 0;
        int pos = 0;
        while (pos < input.Length)
        {
            int chunkSize = Math.Min(ChunkLen, input.Length - pos);
            var chunk = input.Slice(pos, chunkSize);
            bool isLastChunk = pos + chunkSize == input.Length;

            Iv.CopyTo(chunkCv);
            int blocks = (chunkSize + BlockLen - 1) / BlockLen;
            for (int b = 0; b < blocks; b++)
            {
                int boff = b * BlockLen;
                int take = Math.Min(BlockLen, chunkSize - boff);
                uint flags = (b == 0 ? ChunkStart : 0u) | (b == blocks - 1 ? ChunkEnd : 0u);
                if (isLastChunk && cvCount == 0 && b == blocks - 1)
                {
                    Compress(chunkCv, chunk.Slice(boff, take), take, chunkIdx, flags | RootFlag, blockOut);
                    for (int i = 0; i < 4; i++)
                        BinaryPrimitives.WriteUInt32LittleEndian(out16Bytes.Slice(i * 4, 4), blockOut[i]);
                    return;
                }
                Compress(chunkCv, chunk.Slice(boff, take), take, chunkIdx, flags, blockOut);
                blockOut[..8].CopyTo(chunkCv);
            }

            if (isLastChunk)
            {
                chunkCv.CopyTo(rootCv);
                while (cvCount > 0)
                {
                    cvCount--;
                    var left = cvStack.Slice(cvCount * 8, 8);
                    for (int i = 0; i < 8; i++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(rootBlock.Slice(i * 4, 4), left[i]);
                        BinaryPrimitives.WriteUInt32LittleEndian(rootBlock.Slice((i + 8) * 4, 4), rootCv[i]);
                    }
                    uint pFlags = ParentFlag | (cvCount == 0 ? RootFlag : 0u);
                    Compress(Iv, rootBlock, BlockLen, 0, pFlags, blockOut);
                    blockOut[..8].CopyTo(rootCv);
                }
                for (int i = 0; i < 4; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(out16Bytes.Slice(i * 4, 4), blockOut[i]);
                return;
            }

            chunkCv.CopyTo(cvStack.Slice(cvCount * 8, 8));
            cvCount++;
            ulong totalChunks = chunkIdx + 1;
            while ((totalChunks & 1) == 0)
            {
                var right = cvStack.Slice((cvCount - 1) * 8, 8);
                var left = cvStack.Slice((cvCount - 2) * 8, 8);
                for (int i = 0; i < 8; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(rootBlock.Slice(i * 4, 4), left[i]);
                    BinaryPrimitives.WriteUInt32LittleEndian(rootBlock.Slice((i + 8) * 4, 4), right[i]);
                }
                Compress(Iv, rootBlock, BlockLen, 0, ParentFlag, blockOut);
                blockOut[..8].CopyTo(cvStack.Slice((cvCount - 2) * 8, 8));
                cvCount--;
                totalChunks >>= 1;
            }
            chunkIdx++;
            pos += chunkSize;
        }
    }
}
