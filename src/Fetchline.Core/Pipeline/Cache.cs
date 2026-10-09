namespace Fetchline.Core.Pipeline;

/// <summary>
/// The shape of a cache and what a miss costs. A cache is so many sets, each of so many ways,
/// of blocks of so many bytes; with one way it is direct-mapped, and with one set it is fully
/// associative.
/// </summary>
/// <param name="Sets">How many sets: a power of two.</param>
/// <param name="Ways">How many blocks a set holds.</param>
/// <param name="BlockBytes">The size of a block: a power of two, and at least a word.</param>
/// <param name="MissPenalty">How many cycles an instruction waits for a block that is not there.</param>
public sealed record CacheConfig(int Sets, int Ways, int BlockBytes, int MissPenalty)
{
    public const int MaxSets = 1024;

    public const int MaxWays = 8;

    public const int MaxBlockBytes = 256;

    public const int MaxMissPenalty = 64;

    /// <summary>How much the cache holds, in bytes.</summary>
    public int Bytes => Sets * Ways * BlockBytes;

    /// <summary>The block an address is in: the address with its offset in the block cut off.</summary>
    public uint BlockOf(uint address) => address / (uint)BlockBytes;

    /// <summary>The set an address goes to.</summary>
    public int SetOf(uint address) => (int)(BlockOf(address) % (uint)Sets);

    /// <summary>What tells one block from another in the same set.</summary>
    public uint TagOf(uint address) => BlockOf(address) / (uint)Sets;

    /// <summary>The address of the first byte of the block with a tag in a set.</summary>
    public uint AddressOf(uint tag, int set) => (tag * (uint)Sets + (uint)set) * (uint)BlockBytes;

    /// <summary>Throws if a cache cannot be built this way.</summary>
    public void Validate()
    {
        if (Sets is < 1 or > MaxSets || (Sets & (Sets - 1)) != 0)
        {
            throw new ArgumentException($"A cache has a power-of-two number of sets up to {MaxSets}, not {Sets}.");
        }

        if (Ways is < 1 or > MaxWays)
        {
            throw new ArgumentException($"A set has from 1 to {MaxWays} ways, not {Ways}.");
        }

        if (BlockBytes is < 4 or > MaxBlockBytes || (BlockBytes & (BlockBytes - 1)) != 0)
        {
            throw new ArgumentException(
                $"A block is a power-of-two number of bytes from 4 to {MaxBlockBytes}, not {BlockBytes}.");
        }

        if (MissPenalty is < 1 or > MaxMissPenalty)
        {
            throw new ArgumentException($"A miss costs from 1 to {MaxMissPenalty} cycles, not {MissPenalty}.");
        }
    }
}

/// <summary>What became of one access to a cache.</summary>
/// <param name="Hit">The block was there.</param>
/// <param name="Set">The set the address goes to.</param>
/// <param name="Way">The way the block was found in, or was brought into.</param>
/// <param name="Tag">The tag of the block.</param>
/// <param name="Evicted">A block was put out to make room.</param>
/// <param name="EvictedTag">The tag of the block put out.</param>
public readonly record struct CacheAccess(bool Hit, int Set, int Way, uint Tag, bool Evicted, uint EvictedTag);

/// <summary>
/// A cache, as far as time is concerned: which blocks it holds, and which of them was used
/// longest ago. It holds no data. The machine's memory has the data whatever the cache says, so
/// a cache can make a program slower or faster and can never make it wrong.
///
/// A block that is not there is brought into an empty way if the set has one, the lowest
/// numbered first, and otherwise over the way that was used least recently.
/// </summary>
public sealed class Cache
{
    private readonly uint[] _tags;
    private readonly bool[] _valid;

    // When each way was last used, counted in accesses to this cache: a clock of its own, so
    // that what the cache does depends on the order of the accesses and on nothing else.
    private readonly ulong[] _used;
    private ulong _accesses;

    public Cache(CacheConfig config)
    {
        config.Validate();
        Config = config;
        _tags = new uint[config.Sets * config.Ways];
        _valid = new bool[config.Sets * config.Ways];
        _used = new ulong[config.Sets * config.Ways];
    }

    public CacheConfig Config { get; }

    /// <summary>Looks an address up, and brings its block in if it is not there.</summary>
    public CacheAccess Access(uint address)
    {
        var (set, tag) = (Config.SetOf(address), Config.TagOf(address));
        var first = set * Config.Ways;
        _accesses++;

        var victim = -1;
        for (var way = 0; way < Config.Ways; way++)
        {
            var slot = first + way;
            if (_valid[slot] && _tags[slot] == tag)
            {
                _used[slot] = _accesses;
                return new CacheAccess(true, set, way, tag, false, 0);
            }

            // The way to fill is the first empty one; with none empty, the one used longest ago.
            if (victim < 0 || (_valid[first + victim] && (!_valid[slot] || _used[slot] < _used[first + victim])))
            {
                victim = way;
            }
        }

        var into = first + victim;
        var (evicted, old) = (_valid[into], _tags[into]);
        (_valid[into], _tags[into], _used[into]) = (true, tag, _accesses);
        return new CacheAccess(false, set, victim, tag, evicted, evicted ? old : 0);
    }

    /// <summary>Whether a way of a set holds a block, and which.</summary>
    public (bool Valid, uint Tag) Line(int set, int way) => (_valid[set * Config.Ways + way], _tags[set * Config.Ways + way]);
}
