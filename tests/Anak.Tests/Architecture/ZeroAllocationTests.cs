using Anak.Core;
using Xunit;

namespace Anak.Tests.Architecture;

/// <summary>
/// Architectural regression tests asserting zero heap allocations in critical core engine hot paths:
/// legal move generation, check detection, and pseudo-legal attack lookups.
/// </summary>
public class ZeroAllocationTests
{
    [Theory]
    [InlineData(Fen.StartPosition)]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    public void GenerateLegalMoves_AllocatesZeroBytes(string fen)
    {
        Board board = Fen.Parse(fen);
        MoveList buffer = default;

        // Warm up JIT and any static initialization
        board.GenerateLegalMoves(ref buffer);

        long allocated = -1;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                buffer.Clear();
                board.GenerateLegalMoves(ref buffer);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            allocated = after - before;
            if (allocated == 0) break;
        }

        Assert.True(buffer.Count > 0);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(Fen.StartPosition)]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    public void IsCheck_AllocatesZeroBytes(string fen)
    {
        Board board = Fen.Parse(fen);

        // Warm up JIT
        _ = board.IsCheck();
        _ = board.IsInCheck();

        long allocated = -1;
        bool check = false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            check = false;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                check |= board.IsCheck();
                check |= board.IsInCheck();
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            allocated = after - before;
            if (allocated == 0) break;
        }

        Assert.False(check);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(Fen.StartPosition)]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    public void Attacks_AllocatesZeroBytes(string fen)
    {
        Board board = Fen.Parse(fen);
        ulong occ = board.Occupied;

        // Warm up JIT and lookups across all squares and colors
        for (int sq = 0; sq < 64; sq++)
        {
            Square s = (Square)sq;
            _ = board.IsSquareAttacked(s, Color.White);
            _ = board.IsSquareAttacked(s, Color.Black);
            _ = Bitboard.KnightAttacks[sq];
            _ = Bitboard.KingAttacks[sq];
            _ = Bitboard.PawnAttacks(s, Color.White);
            _ = MagicBitboards.GetBishopAttacks(s, occ);
            _ = MagicBitboards.GetRookAttacks(s, occ);
            _ = MagicBitboards.GetQueenAttacks(s, occ);
        }

        long allocated = -1;
        ulong accumulator = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            accumulator = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                for (int sq = 0; sq < 64; sq++)
                {
                    Square s = (Square)sq;
                    if (board.IsSquareAttacked(s, Color.White)) accumulator++;
                    if (board.IsSquareAttacked(s, Color.Black)) accumulator++;
                    accumulator ^= Bitboard.KnightAttacks[sq];
                    accumulator ^= Bitboard.KingAttacks[sq];
                    accumulator ^= Bitboard.PawnAttacks(s, Color.White);
                    accumulator ^= MagicBitboards.GetBishopAttacks(s, occ);
                    accumulator ^= MagicBitboards.GetRookAttacks(s, occ);
                    accumulator ^= MagicBitboards.GetQueenAttacks(s, occ);
                }
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            allocated = after - before;
            if (allocated == 0) break;
        }

        Assert.True(accumulator > 0);
        Assert.Equal(0, allocated);
    }
}
