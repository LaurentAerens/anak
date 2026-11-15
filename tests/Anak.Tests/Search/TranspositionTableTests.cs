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

        tt.Store(key, move, depth: 8, score: 65, age: tt.CurrentAge, flags: TTFlags.Exact, expertId: 0, staticEval: 42);

        bool found = tt.TryGet(key, out TTEntry entry);
        Assert.True(found);
        Assert.Equal(key, entry.Key);
        Assert.Equal(move, entry.BestMove);
        Assert.Equal(8, entry.Depth);
        Assert.Equal(65, entry.Score);
        Assert.Equal(42, entry.StaticEval);
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

        tt.Store(key1, move1, depth: 5, score: 10, age: tt.CurrentAge, flags: TTFlags.Exact);
        tt.Store(key2, move2, depth: 7, score: 20, age: tt.CurrentAge, flags: TTFlags.LowerBound);

        Assert.True(tt.TryGet(key1, out var e1));
        Assert.Equal(move1, e1.BestMove);
        Assert.Equal(5, e1.Depth);

        Assert.True(tt.TryGet(key2, out var e2));
        Assert.Equal(move2, e2.BestMove);
        Assert.Equal(7, e2.Depth);
    }

    [Fact]
    public void TryGet_NonExistentKey_ReturnsFalse()
    {
        var tt = new TranspositionTable(1);
        Assert.False(tt.TryGet(0xCAFEBABEUL, out _));
    }

    [Fact]
    public void SameKey_UpdatesWhenDeeperOrExact()
    {
        var tt = new TranspositionTable(1);
        ulong key = 0xDEADBEEFCAFE1234UL;
        Move move1 = new Move(Square.G1, Square.F3);
        Move move2 = new Move(Square.E2, Square.E4);

        tt.Store(key, move1, depth: 4, score: 40, age: 1, flags: TTFlags.LowerBound);
        tt.Store(key, move2, depth: 6, score: 80, age: 1, flags: TTFlags.Exact);

        Assert.True(tt.TryGet(key, out var updated));
        Assert.Equal(key, updated.Key);
        Assert.Equal(move2, updated.BestMove);
        Assert.Equal(6, updated.Depth);
        Assert.Equal(80, updated.Score);
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
        Assert.Equal(tt.ClusterCount * 3, tt.EntryCount);
    }

    [Fact]
    public void Clear_EmptiesAllEntries()
    {
        var tt = new TranspositionTable(1);
        ulong key = 0x1234UL;
        tt.Store(key, new Move(Square.E2, Square.E4), depth: 4, score: 50, age: 1, flags: TTFlags.Exact);

        Assert.True(tt.TryGet(key, out _));

        tt.Clear();

        Assert.False(tt.TryGet(key, out _));
    }

    [Fact]
    public void ReplacementPolicy_ReplacesOlderAgeEntry()
    {
        var tt = new TranspositionTable(1);
        ulong step = tt.ClusterCount;

        ulong key1 = step * 1;
        ulong key2 = step * 2;
        ulong key3 = step * 3;
        ulong key4 = step * 4;

        // Fill all 3 slots in cluster 0: key1 has old age 1, key2 and key3 have current age 2
        tt.Store(key1, new Move(Square.A2, Square.A3), depth: 10, score: 10, age: 1, flags: TTFlags.Exact);
        tt.Store(key2, new Move(Square.B2, Square.B3), depth: 5, score: 20, age: 2, flags: TTFlags.Exact);
        tt.Store(key3, new Move(Square.C2, Square.C3), depth: 8, score: 30, age: 2, flags: TTFlags.Exact);

        // Inserting key4 with age 2 should replace key1 (older age 1), preserving key2 and key3
        tt.Store(key4, new Move(Square.D2, Square.D3), depth: 4, score: 40, age: 2, flags: TTFlags.Exact);

        Assert.True(tt.TryGet(key4, out _));
        Assert.True(tt.TryGet(key2, out _));
        Assert.True(tt.TryGet(key3, out _));
        Assert.False(tt.TryGet(key1, out _));
    }

    [Fact]
    public void HashfullPermille_ReportsOccupancy()
    {
        var tt = new TranspositionTable(1);
        Assert.Equal(0, tt.HashfullPermille());

        for (ulong i = 1; i <= 50; i++)
        {
            tt.Store(i * 10007, new Move(Square.E2, Square.E4), depth: 3, score: 10, age: tt.CurrentAge, flags: TTFlags.Exact);
        }

        int hashfull = tt.HashfullPermille();
        Assert.InRange(hashfull, 0, 1000);
    }

    [Fact]
    public void CollisionHandling_ThreeKeysSameCluster_AllPreserved()
    {
        var tt = new TranspositionTable(1);
        ulong step = tt.ClusterCount;

        ulong key1 = 5UL;
        ulong key2 = 5UL + step;
        ulong key3 = 5UL + (2 * step);

        Move move1 = new Move(Square.E2, Square.E4);
        Move move2 = new Move(Square.D2, Square.D4);
        Move move3 = new Move(Square.C2, Square.C4);

        tt.Store(key1, move1, depth: 5, score: 10, age: tt.CurrentAge, flags: TTFlags.Exact);
        tt.Store(key2, move2, depth: 6, score: 20, age: tt.CurrentAge, flags: TTFlags.Exact);
        tt.Store(key3, move3, depth: 7, score: 30, age: tt.CurrentAge, flags: TTFlags.Exact);

        Assert.True(tt.TryGet(key1, out var e1));
        Assert.Equal(move1, e1.BestMove);

        Assert.True(tt.TryGet(key2, out var e2));
        Assert.Equal(move2, e2.BestMove);

        Assert.True(tt.TryGet(key3, out var e3));
        Assert.Equal(move3, e3.BestMove);
    }

    [Fact]
    public void StoreAndRetrieve_ExpertId_PreservedAndIsolated()
    {
        var tt = new TranspositionTable(1);
        ulong key = 0xFEEDFACECAFEBEEFUL;
        Move move = new Move(Square.E2, Square.E4);

        tt.Store(key, move, depth: 6, score: 120, age: tt.CurrentAge, flags: TTFlags.Exact, expertId: 4);

        Assert.True(tt.TryGet(key, out var entry));
        Assert.Equal(key, entry.Key);
        Assert.Equal(move, entry.BestMove);
        Assert.Equal(6, entry.Depth);
        Assert.Equal(120, entry.Score);
        Assert.Equal(4, entry.ExpertId);

        Move move2 = new Move(Square.D2, Square.D4);
        tt.Store(key, move2, depth: 7, score: 85, age: tt.CurrentAge, flags: TTFlags.Exact, expertId: 0);

        Assert.True(tt.TryGet(key, out var updated));
        Assert.Equal(move2, updated.BestMove);
        Assert.Equal(7, updated.Depth);
        Assert.Equal(85, updated.Score);
        Assert.Equal(0, updated.ExpertId);
    }
}
