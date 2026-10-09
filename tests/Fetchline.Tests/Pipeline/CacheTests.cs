using Fetchline.Core.Pipeline;
using Fetchline.Tests.Support;

namespace Fetchline.Tests.Pipeline;

/// <summary>The cache on its own: where an address goes, what is there, and what is put out.</summary>
public class CacheTests
{
    private const ulong Seed = 0xCAC4_E000;

    [Fact]
    public void AnAddressIsATagASetAndAnOffset()
    {
        // 8 sets of 16-byte blocks: four bits of offset, three of set, the rest is the tag.
        var config = new CacheConfig(Sets: 8, Ways: 2, BlockBytes: 16, MissPenalty: 10);

        Assert.Equal(256, config.Bytes);
        Assert.Equal((0x1234_567u, 7, 0x2468_ACu), (config.BlockOf(0x1234_5678), config.SetOf(0x1234_5678), config.TagOf(0x1234_5678)));
        Assert.Equal(0x1234_5670u, config.AddressOf(0x2468_AC, 7));

        // Every byte of a block is in the same set with the same tag; the next byte is not.
        Assert.Equal((config.SetOf(0x40), config.TagOf(0x40)), (config.SetOf(0x4F), config.TagOf(0x4F)));
        Assert.NotEqual(config.SetOf(0x4F), config.SetOf(0x50));

        // The top of memory does not wrap round.
        Assert.Equal(0xFFFF_FFF0u, config.AddressOf(config.TagOf(0xFFFF_FFFF), config.SetOf(0xFFFF_FFFF)));
    }

    [Theory]
    [InlineData(0, 1, 4, 1)]
    [InlineData(3, 1, 4, 1)]        // not a power of two
    [InlineData(2048, 1, 4, 1)]
    [InlineData(4, 0, 4, 1)]
    [InlineData(4, 9, 4, 1)]
    [InlineData(4, 1, 2, 1)]        // smaller than a word
    [InlineData(4, 1, 24, 1)]
    [InlineData(4, 1, 512, 1)]
    [InlineData(4, 1, 4, 0)]
    [InlineData(4, 1, 4, 65)]
    public void ACacheThatCannotBeBuiltIsRefused(int sets, int ways, int block, int penalty)
    {
        var config = new CacheConfig(sets, ways, block, penalty);

        Assert.Throws<ArgumentException>(config.Validate);
        Assert.Throws<ArgumentException>(() => new Cache(config));
    }

    [Fact]
    public void ADirectMappedCacheHoldsOneBlockInEachSet()
    {
        var cache = new Cache(new CacheConfig(Sets: 4, Ways: 1, BlockBytes: 4, MissPenalty: 1));

        // 0x00 and 0x10 go to the same set, and each puts the other out; 0x04 is a set of its own.
        Assert.Equal(new CacheAccess(false, 0, 0, 0, false, 0), cache.Access(0x00));
        Assert.Equal(new CacheAccess(true, 0, 0, 0, false, 0), cache.Access(0x03));
        Assert.Equal(new CacheAccess(false, 1, 0, 0, false, 0), cache.Access(0x04));
        Assert.Equal(new CacheAccess(false, 0, 0, 1, true, 0), cache.Access(0x10));
        Assert.Equal(new CacheAccess(false, 0, 0, 0, true, 1), cache.Access(0x00));
        Assert.True(cache.Access(0x04).Hit);

        Assert.Equal((true, 0u), cache.Line(0, 0));
        Assert.Equal((false, 0u), cache.Line(2, 0));
    }

    [Fact]
    public void WithTwoWaysTheOneUsedLongestAgoIsPutOut()
    {
        var cache = new Cache(new CacheConfig(Sets: 1, Ways: 2, BlockBytes: 4, MissPenalty: 1));
        const uint A = 0x00, B = 0x04, C = 0x08;

        // An empty way is filled first, the lowest numbered first.
        Assert.Equal((false, 0, false), (cache.Access(A).Hit, cache.Access(A).Way, cache.Access(A).Evicted));
        Assert.Equal(new CacheAccess(false, 0, 1, 1, false, 0), cache.Access(B));

        // A was used after B, so C puts B out; then B puts A out, which is now the older.
        Assert.True(cache.Access(A).Hit);
        Assert.Equal(new CacheAccess(false, 0, 1, 2, true, 1), cache.Access(C));
        Assert.Equal(new CacheAccess(false, 0, 0, 1, true, 0), cache.Access(B));
        Assert.True(cache.Access(C).Hit && cache.Access(B).Hit);
        Assert.False(cache.Access(A).Hit);
    }

    [Fact]
    public void OneBlockMoreThanTheWaysMissesEveryTimeRoundAndOneFewerNever()
    {
        var cache = new Cache(new CacheConfig(Sets: 1, Ways: 4, BlockBytes: 8, MissPenalty: 1));

        // Four blocks in four ways: each misses once and never again.
        var four = Enumerable.Range(0, 40).Select(i => cache.Access((uint)(i % 4) * 8).Hit).ToList();
        Assert.Equal(4, four.Count(hit => !hit));
        Assert.All(four.Skip(4), Assert.True);

        // Five blocks taken in turn: the one wanted is always the one that was just put out.
        var five = Enumerable.Range(0, 40).Select(i => cache.Access(0x100 + (uint)(i % 5) * 8).Hit).ToList();
        Assert.All(five, Assert.False);
    }

    [Fact]
    public void ItAgreesWithASecondStatementOfTheRules()
    {
        // The rules again, as a list for each set kept in the order of use: a block that is in
        // the list is a hit and goes to the end; one that is not goes to the end too, and if the
        // list is then longer than the ways, what is at the front is put out.
        (int Sets, int Ways, int Block)[] shapes = [(1, 1, 4), (4, 1, 4), (8, 2, 16), (1, 4, 8), (16, 4, 32), (2, 8, 4), (64, 1, 64)];
        var random = new SeededRandom(Seed);
        foreach (var (sets, ways, block) in shapes)
        {
            var config = new CacheConfig(sets, ways, block, MissPenalty: 1);
            var cache = new Cache(config);
            var order = Enumerable.Range(0, sets).Select(_ => new List<uint>()).ToArray();

            // Addresses from a few blocks more than the cache holds, so that it is always full
            // and always choosing.
            var blocks = sets * ways * 2 + 3;
            for (var i = 0; i < 4000; i++)
            {
                var address = (uint)(random.Next(0, blocks - 1) * block + random.Next(0, block - 1));
                var (set, tag) = (config.SetOf(address), config.TagOf(address));
                var list = order[set];
                var hit = list.Remove(tag);
                list.Add(tag);
                uint? out_ = list.Count > ways ? list[0] : null;
                if (out_ is not null)
                {
                    list.RemoveAt(0);
                }

                var access = cache.Access(address);
                var where = $"{sets} sets, {ways} ways, {block} bytes, access {i} (seed 0x{Seed:X})";
                Assert.True(access.Hit == hit && access.Set == set && access.Tag == tag, where);
                Assert.True(access.Evicted == (out_ is not null) && (out_ is null || access.EvictedTag == out_), where);
                Assert.True(cache.Line(set, access.Way) == (true, tag), where);
            }
        }
    }
}
