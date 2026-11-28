using System.Buffers.Binary;
using System.Text;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Path of Exile 3.29 hashing functions verified against <c>Game.exe</c> and stock <c>.datc64</c> / <c>Bundles2</c>.
/// </summary>
public static class PoeHashing
{
    public const ulong BundleIndexSeed = 0x1337B33FUL;

    /// <summary>
    /// MurmurHash2 (32-bit, seed 0) over narrow UTF-8 bytes (<c>Game.exe 0x140df6e10</c>).
    /// Used for <c>MtxTypes.HASH32</c>, <c>MtxTypeGameSpecific.HASH32</c>, and <c>0x02bb</c> object type hashes.
    /// </summary>
    public static uint Hash32(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MurmurHash2(Encoding.UTF8.GetBytes(text), seed: 0);
    }

    /// <summary>
    /// 16-bit XOR fold used for <c>Pet.HASH16</c>: <c>(HASH32 &gt;&gt; 16) ^ (HASH32 &amp; 0xFFFF)</c>.
    /// </summary>
    public static ushort FoldHash16(uint hash32) => (ushort)(((hash32 >> 16) ^ (hash32 & 0xFFFF)) & 0xFFFF);

    /// <summary>
    /// 32-bit MurmurHash2 over raw bytes.
    /// </summary>
    public static uint MurmurHash2(ReadOnlySpan<byte> data, uint seed = 0)
    {
        const uint m = 0x5BD1E995u;
        const int r = 24;

        uint h = seed ^ (uint)data.Length;
        int offset = 0;

        while (offset + 4 <= data.Length)
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            k *= m;
            k ^= k >> r;
            k *= m;

            h *= m;
            h ^= k;
            offset += 4;
        }

        int remaining = data.Length - offset;
        if (remaining == 3)
        {
            h ^= (uint)data[offset + 2] << 16;
        }

        if (remaining >= 2)
        {
            h ^= (uint)data[offset + 1] << 8;
        }

        if (remaining >= 1)
        {
            h ^= data[offset];
            h *= m;
        }

        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return h;
    }

    /// <summary>
    /// MurmurHash64A used by PoE 3.29 <c>Bundles2/_.index.bin</c> file path hashing (seed <c>0x1337b33f</c>).
    /// </summary>
    public static ulong MurmurHash64A(ReadOnlySpan<byte> data, ulong seed = BundleIndexSeed)
    {
        const ulong m = 0xC6A4A7935BD1E995UL;
        const int r = 47;

        ulong h = seed ^ ((ulong)data.Length * m);
        int offset = 0;

        while (offset + 8 <= data.Length)
        {
            ulong k = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
            k *= m;
            k ^= k >> r;
            k *= m;

            h ^= k;
            h *= m;
            offset += 8;
        }

        int remaining = data.Length - offset;
        if (remaining >= 7) h ^= (ulong)data[offset + 6] << 48;
        if (remaining >= 6) h ^= (ulong)data[offset + 5] << 40;
        if (remaining >= 5) h ^= (ulong)data[offset + 4] << 32;
        if (remaining >= 4) h ^= (ulong)data[offset + 3] << 24;
        if (remaining >= 3) h ^= (ulong)data[offset + 2] << 16;
        if (remaining >= 2) h ^= (ulong)data[offset + 1] << 8;
        if (remaining >= 1)
        {
            h ^= data[offset];
            h *= m;
        }

        h ^= h >> r;
        h *= m;
        h ^= h >> r;
        return h;
    }

    /// <summary>
    /// Computes the <c>Bundles2/_.index.bin</c> path hash (lowercase forward-slash path, UTF-8).
    /// </summary>
    public static ulong HashBundlePath(string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(virtualPath);
        string normalized = virtualPath.Replace('\\', '/').ToLowerInvariant();
        return MurmurHash64A(Encoding.UTF8.GetBytes(normalized), BundleIndexSeed);
    }
}
