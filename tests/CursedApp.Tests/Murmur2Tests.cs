using CursedApp.Services;

namespace CursedApp.Tests;

public class Murmur2Tests
{
    /// <summary>
    /// SMHasher's verification value for MurmurHash2 x86_32. It hashes keys of
    /// every length from 0 to 255 with a length-derived seed, then hashes the
    /// concatenated results. Getting 0x27864C1E back proves the mixing constants,
    /// the tail handling and the byte order are all correct — which matters,
    /// because a subtly wrong hash would simply fail to match any addon rather
    /// than throw.
    /// </summary>
    [Fact]
    public void Hash_MatchesSmhasherVerificationValue()
    {
        const uint expected = 0x27864C1E;

        var key = new byte[256];
        var hashes = new byte[4 * 256];

        for (var i = 0; i < 256; i++)
        {
            key[i] = (byte)i;
            var hash = Murmur2.Hash(key.AsSpan(0, i), seed: (uint)(256 - i));
            BitConverter.TryWriteBytes(hashes.AsSpan(i * 4, 4), hash);
        }

        var final = Murmur2.Hash(hashes, seed: 0);

        Assert.Equal(expected, final);
    }

    [Fact]
    public void Hash_OfEmptyInput_IsSeedDerived()
    {
        // With no bytes to mix, the result is the finalizer applied to the seed
        // alone, so different seeds must still give different hashes.
        Assert.NotEqual(Murmur2.Hash([], seed: 0), Murmur2.Hash([], seed: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)]
    public void Hash_HandlesEveryTailLength(int length)
    {
        var data = new byte[length];
        Random.Shared.NextBytes(data);

        // Two calls with the same input must agree; the switch/goto tail must not
        // read past the end for any remainder.
        Assert.Equal(Murmur2.Hash(data), Murmur2.Hash(data));
    }

    [Fact]
    public void Normalize_DropsOnlyTabLfCrAndSpace()
    {
        var source = " a\tb\r\nc "u8.ToArray();
        var destination = new byte[source.Length];

        var written = Murmur2.Normalize(source, destination);

        Assert.Equal(3, written);
        Assert.Equal("abc"u8.ToArray(), destination[..written]);
    }

    /// <summary>
    /// The whole point of normalization: the same addon file checked out with
    /// CRLF or LF endings must produce the same fingerprint.
    /// </summary>
    [Fact]
    public void HashNormalized_IgnoresLineEndingStyle()
    {
        var crlf = "## Interface: 110002\r\n## Title: Test\r\n"u8;
        var lf = "## Interface: 110002\n## Title: Test\n"u8;

        Assert.Equal(Murmur2.HashNormalized(crlf), Murmur2.HashNormalized(lf));
    }

    [Fact]
    public void HashNormalized_UsesPooledBufferForLargeInput()
    {
        // Crosses the 8 KB stackalloc threshold, exercising the ArrayPool path.
        var large = new byte[20_000];
        Random.Shared.NextBytes(large);

        var padded = new byte[large.Length * 2];
        for (var i = 0; i < large.Length; i++)
        {
            padded[i * 2] = large[i];
            padded[(i * 2) + 1] = (byte)' ';
        }

        Assert.Equal(Murmur2.HashNormalized(large), Murmur2.HashNormalized(padded));
    }
}
