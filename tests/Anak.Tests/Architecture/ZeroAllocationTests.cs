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

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++)
        {
            buffer.Clear();
            board.GenerateLegalMoves(ref buffer);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        long allocated = after - before;

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

        long before = GC.GetAllocatedBytesForCurrentThread();

        bool check = false;
        for (int i = 0; i < 100; i++)
        {
            check |= board.IsCheck();
            check |= board.IsInCheck();
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        long allocated = after - before;

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

        // Warm up JIT and lookups
        _ = board.IsSquareAttacked(Square.E1, Color.Black);
        _ = Bitboard.KnightAttacks[(int)Square.E4];
        _ = Bitboard.KingAttacks[(int)Square.E1];
        _ = Bitboard.PawnAttacks(Square.E2, Color.White);
        _ = MagicBitboards.GetBishopAttacks(Square.C1, occ);
        _ = MagicBitboards.GetRookAttacks(Square.A1, occ);
        _ = MagicBitboards.GetQueenAttacks(Square.D1, occ);

        long before = GC.GetAllocatedBytesForCurrentThread();

        ulong accumulator = 0;
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
        long allocated = after - before;

        Assert.True(accumulator > 0);
        Assert.Equal(0, allocated);
    }
}
