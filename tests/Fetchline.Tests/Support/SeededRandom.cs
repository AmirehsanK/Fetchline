namespace Fetchline.Tests.Support;

/// <summary>
/// The random numbers every randomised test draws from: xoshiro256** seeded through SplitMix64.
/// It is written out here, not taken from <see cref="Random"/>, so that a seed printed by a
/// failing test replays the same sequence on any runtime version.
/// </summary>
internal sealed class SeededRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public SeededRandom(ulong seed)
    {
        Seed = seed;
        _s0 = SplitMix(ref seed);
        _s1 = SplitMix(ref seed);
        _s2 = SplitMix(ref seed);
        _s3 = SplitMix(ref seed);
    }

    /// <summary>The seed this sequence started from, for failure messages.</summary>
    public ulong Seed { get; }

    public ulong NextUInt64()
    {
        var result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    /// <summary>A value from <paramref name="min"/> up to and including <paramref name="max"/>.</summary>
    public int Next(int min, int max)
    {
        var span = (ulong)((long)max - min + 1);
        return (int)(min + (long)(NextUInt64() % span));
    }

    public bool NextBool() => (NextUInt64() & 1) != 0;

    /// <summary>True with a probability of <paramref name="percent"/> in a hundred.</summary>
    public bool Chance(int percent) => Next(0, 99) < percent;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(0, items.Count - 1)];

    private static ulong SplitMix(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
