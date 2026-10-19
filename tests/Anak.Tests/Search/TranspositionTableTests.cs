using Anak.Core;
using Anak.Search;
using Xunit;

namespace Anak.Tests.Search;

public class TranspositionTableTests
{
    [Fact]
    public void StoreAndRetrieve_SingleEntry()
    {
        var tt = new TranspositionTable(1); // 1 MB
        ulong key = 0x123456789ABCDEF0UL;
        Move move = new Move(Square.E2, Square.E4, MoveType.DoublePawnPush);

        tt.Store(key, move, visits: 42, value: 0.65f, prior: 0.8f, age: tt.CurrentAge, flags: TTFlags.Exact);

        bool found = tt.TryGet(key, out TTEntry entry);
        Assert.True(found);
        Assert.Equal(key, entry.Key);
        Assert.Equal(move, entry.BestMove);
        Assert.Equal(42u, entry.Visits);
        Assert.InRange(entry.Value, 0.64f, 0.66f);
        Assert.InRange(entry.Prior, 0.79f, 0.81f);
        Assert.Equal(tt.CurrentAge, entry.Age);
        Assert.Equal(TTFlags.Exact, entry.Flags);
    }

    [Fact]
    public void StoreAndRetrieve_DifferentKeys()
    {
        var tt = new TranspositionTable(1);
        ulong key1 = 0x1111222233334444UL;
        ulong key2 = 0x5555666677778888UL;

        Move move1 = new Move(Square.E2, Square.E4);
        Move move2 = new Move(Square.D2, Square.D4);

        tt.Store(key1, move1, visits: 10, value: 0.1f, prior: 0.5f, age: tt.CurrentAge);
        tt.Store(key2, move2, visits: 20, value: 0.2f, prior: 0.6f, age: tt.CurrentAge);

        Assert.True(tt.TryGet(key1, out var e1));
        Assert.Equal(move1, e1.BestMove);
        Assert.Equal(10u, e1.Visits);

        Assert.True(tt.TryGet(key2, out var e2));
        Assert.Equal(move2, e2.BestMove);
        Assert.Equal(20u, e2.Visits);
    }

    [Fact]
    public void TryGet_NonExistentKey_ReturnsFalse()
    {
        var tt = new TranspositionTable(1);
        Assert.False(tt.TryGet(0xCAFEBABEUL, out _));
    }

    [Fact]
    public void DAG_TranspositionReuse_MergesEntries()
    {
        var tt = new TranspositionTable(1);
        ulong key = 0xDEADBEEFCAFE1234UL;
        Move move1 = new Move(Square.G1, Square.F3);

        tt.Store(key, move1, visits: 10, value: 0.4f, prior: 0.5f, age: 1);

        // Store again with the same key but different visits and value
        tt.Store(key, move1, visits: 30, value: 0.8f, prior: 0.5f, age: 1);

        Assert.True(tt.TryGet(key, out var merged));
        Assert.Equal(key, merged.Key);
        Assert.Equal(30u, merged.Visits);
        // Weighted average: (0.4 * 10 + 0.8 * 30) / 40 = (4 + 24) / 40 = 28 / 40 = 0.70
        Assert.InRange(merged.Value, 0.69f, 0.71f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    public void Resize_AdjustsCapacity(int sizeMb)
    {
        var tt = new TranspositionTable(1);
        tt.Resize(sizeMb);

        Assert.True(tt.SizeMb >= 1);
        Assert.True(tt.ClusterCount > 0);
        Assert.Equal(tt.ClusterCount * 2, tt.EntryCount);
    }

    [Fact]
    public void Clear_EmptiesAllEntries()
    {
        var tt = new TranspositionTable(1);
        ulong key = 0x1234UL;
        tt.Store(key, new Move(Square.E2, Square.E4), visits: 10, value: 0.5f, prior: 0.5f, age: 1);

        Assert.True(tt.TryGet(key, out _));

        tt.Clear();

        Assert.False(tt.TryGet(key, out _));
    }

    [Fact]
    public void ReplacementPolicy_ReplacesOlderAgeEntry()
    {
        var tt = new TranspositionTable(1);
        // Target index 0 by using keys with low bits 0
        ulong mask = tt.ClusterCount - 1;
        ulong step = tt.ClusterCount;

        ulong key1 = step * 1;
        ulong key2 = step * 2;
        ulong key3 = step * 3;

        // Store key1 with old age 1
        tt.Store(key1, new Move(Square.A2, Square.A3), visits: 100, value: 0.1f, prior: 0.5f, age: 1);
        // Store key2 with current age 2
        tt.Store(key2, new Move(Square.B2, Square.B3), visits: 5, value: 0.2f, prior: 0.5f, age: 2);

        // Both slots in the cluster are now occupied.
        // Inserting key3 with age 2 should replace key1 (older age 1), preserving key2 (current age 2)
        tt.Store(key3, new Move(Square.C2, Square.C3), visits: 10, value: 0.3f, prior: 0.5f, age: 2);

        Assert.True(tt.TryGet(key3, out _));
        Assert.True(tt.TryGet(key2, out _));
        Assert.False(tt.TryGet(key1, out _));
    }

    [Fact]
    public void HashfullPermille_ReportsOccupancy()
    {
        var tt = new TranspositionTable(1);
        Assert.Equal(0, tt.HashfullPermille());

        // Fill some entries with current age
        for (ulong i = 1; i <= 50; i++)
        {
            tt.Store(i * 10007, new Move(Square.E2, Square.E4), visits: 1, value: 0.1f, prior: 0.1f, age: tt.CurrentAge);
        }

        int hashfull = tt.HashfullPermille();
        Assert.InRange(hashfull, 0, 1000);
    }

    [Fact]
    public void CollisionHandling_TwoKeysSameCluster_BothPreserved()
    {
        var tt = new TranspositionTable(1);
        ulong step = tt.ClusterCount;

        // Two keys mapping to the exact same cluster index (e.g. index 5)
        ulong key1 = 5UL;
        ulong key2 = 5UL + step;

        Move move1 = new Move(Square.E2, Square.E4);
        Move move2 = new Move(Square.D2, Square.D4);

        tt.Store(key1, move1, visits: 10, value: 0.1f, prior: 0.5f, age: tt.CurrentAge);
        tt.Store(key2, move2, visits: 20, value: 0.2f, prior: 0.6f, age: tt.CurrentAge);

        // Both slots in the cluster should store their respective key
        Assert.True(tt.TryGet(key1, out var e1));
        Assert.Equal(key1, e1.Key);
        Assert.Equal(move1, e1.BestMove);

        Assert.True(tt.TryGet(key2, out var e2));
        Assert.Equal(key2, e2.Key);
        Assert.Equal(move2, e2.BestMove);
    }
}

