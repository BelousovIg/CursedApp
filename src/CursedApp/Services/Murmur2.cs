namespace CursedApp.Services;

/// <summary>
/// MurmurHash2 (32-bit), the hash CurseForge uses to fingerprint addon files.
/// The "normalized" variant drops tab, LF, CR and space bytes before hashing,
/// which is what makes the fingerprint stable across line-ending changes.
/// </summary>
public static class Murmur2
{
    private const uint M = 0x5bd1e995;
    private const int R = 24;

    /// <summary>The seed CurseForge fingerprints are computed with.</summary>
    public const uint CurseSeed = 1;

    public static uint Hash(ReadOnlySpan<byte> data, uint seed = CurseSeed)
    {
        var length = data.Length;
        var h = seed ^ (uint)length;
        var index = 0;

        while (length >= 4)
        {
            var k = (uint)(data[index] | (data[index + 1] << 8) | (data[index + 2] << 16) | (data[index + 3] << 24));

            k *= M;
            k ^= k >> R;
            k *= M;

            h *= M;
            h ^= k;

            index += 4;
            length -= 4;
        }

        switch (length)
        {
            case 3:
                h ^= (uint)(data[index + 2] << 16);
                goto case 2;
            case 2:
                h ^= (uint)(data[index + 1] << 8);
                goto case 1;
            case 1:
                h ^= data[index];
                h *= M;
                break;
        }

        h ^= h >> 13;
        h *= M;
        h ^= h >> 15;

        return h;
    }

    /// <summary>
    /// Copies <paramref name="source"/> minus the whitespace bytes CurseForge ignores.
    /// Returns the length written into <paramref name="destination"/>.
    /// </summary>
    public static int Normalize(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var written = 0;
        foreach (var b in source)
        {
            if (b is 9 or 10 or 13 or 32)
                continue;

            destination[written++] = b;
        }

        return written;
    }

    public static uint HashNormalized(ReadOnlySpan<byte> source, uint seed = CurseSeed)
    {
        byte[]? rented = null;
        try
        {
            var buffer = source.Length <= 8192
                ? stackalloc byte[source.Length]
                : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(source.Length)).AsSpan(0, source.Length);

            var length = Normalize(source, buffer);
            return Hash(buffer[..length], seed);
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
