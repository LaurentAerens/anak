using System.Numerics;
using Anak.Core;
using Xunit;

namespace Anak.Tests.Core;

public class BitboardTests
{
    [Fact]
    public void PopCount_ReturnsCorrectBitCount()
    {
        Assert.Equal(0, Bitboard.PopCount(Bitboard.Empty));
        Assert.Equal(64, Bitboard.PopCount(Bitboard.All));
        Assert.Equal(8, Bitboard.PopCount(Bitboard.Rank1));
        Assert.Equal(8, Bitboard.PopCount(Bitboard.FileA));
        Assert.Equal(1, Bitboard.PopCount(1UL << (int)Square.E4));
    }

    [Fact]
    public void TrailingZeroCount_And_Lsb_ReturnCorrectSquare()
    {
        Assert.Equal((int)Square.A1, Bitboard.TrailingZeroCount(1UL));
        Assert.Equal(Square.A1, Bitboard.Lsb(1UL));

        ulong bb = 1UL << (int)Square.E4;
        Assert.Equal((int)Square.E4, Bitboard.TrailingZeroCount(bb));
        Assert.Equal(Square.E4, Bitboard.Lsb(bb));

        ulong bbH8 = 1UL << (int)Square.H8;
        Assert.Equal((int)Square.H8, Bitboard.TrailingZeroCount(bbH8));
        Assert.Equal(Square.H8, Bitboard.Lsb(bbH8));
    }

    [Fact]
    public void LeadingZeroCount_And_Msb_ReturnCorrectSquare()
    {
        ulong bb = 1UL << (int)Square.H8;
        Assert.Equal(0, Bitboard.LeadingZeroCount(bb));
        Assert.Equal(Square.H8, Bitboard.Msb(bb));

        ulong bbA1 = 1UL;
        Assert.Equal(63, Bitboard.LeadingZeroCount(bbA1));
        Assert.Equal(Square.A1, Bitboard.Msb(bbA1));
    }

    [Fact]
    public void Set_Clear_Test_WorkCorrectly()
    {
        ulong bb = Bitboard.Empty;
        Assert.False(Bitboard.Test(bb, Square.D4));

        bb = Bitboard.Set(bb, Square.D4);
        Assert.True(Bitboard.Test(bb, Square.D4));
        Assert.Equal(1UL << (int)Square.D4, bb);

        bb = Bitboard.Clear(bb, Square.D4);
        Assert.False(Bitboard.Test(bb, Square.D4));
        Assert.Equal(Bitboard.Empty, bb);
    }

    [Fact]
    public void PopLsb_ClearsLowestBit_And_ReturnsSquare()
    {
        ulong bb = (1UL << (int)Square.C3) | (1UL << (int)Square.F6);
        Square first = Bitboard.PopLsb(ref bb);
        Assert.Equal(Square.C3, first);
        Assert.Equal(1UL << (int)Square.F6, bb);

        Square second = Bitboard.PopLsb(ref bb);
        Assert.Equal(Square.F6, second);
        Assert.Equal(Bitboard.Empty, bb);
    }

    [Fact]
    public void KnightAttacks_Center_HasEightMoves()
    {
        ulong attacks = Bitboard.KnightAttacks[(int)Square.E4];
        Assert.Equal(8, Bitboard.PopCount(attacks));
        Assert.True(Bitboard.Test(attacks, Square.D6));
        Assert.True(Bitboard.Test(attacks, Square.F6));
        Assert.True(Bitboard.Test(attacks, Square.C5));
        Assert.True(Bitboard.Test(attacks, Square.G5));
        Assert.True(Bitboard.Test(attacks, Square.C3));
        Assert.True(Bitboard.Test(attacks, Square.G3));
        Assert.True(Bitboard.Test(attacks, Square.D2));
        Assert.True(Bitboard.Test(attacks, Square.F2));
    }

    [Fact]
    public void KnightAttacks_Corner_HasTwoMoves()
    {
        ulong attacks = Bitboard.KnightAttacks[(int)Square.A1];
        Assert.Equal(2, Bitboard.PopCount(attacks));
        Assert.True(Bitboard.Test(attacks, Square.B3));
        Assert.True(Bitboard.Test(attacks, Square.C2));
    }

    [Fact]
    public void KingAttacks_Center_HasEightMoves()
    {
        ulong attacks = Bitboard.KingAttacks[(int)Square.E4];
        Assert.Equal(8, Bitboard.PopCount(attacks));
    }

    [Fact]
    public void KingAttacks_Corner_HasThreeMoves()
    {
        ulong attacks = Bitboard.KingAttacks[(int)Square.A1];
        Assert.Equal(3, Bitboard.PopCount(attacks));
        Assert.True(Bitboard.Test(attacks, Square.A2));
        Assert.True(Bitboard.Test(attacks, Square.B1));
        Assert.True(Bitboard.Test(attacks, Square.B2));
    }

    [Fact]
    public void PawnAttacks_White_And_Black()
    {
        ulong wpAttacks = Bitboard.PawnAttacks(Square.E4, Color.White);
        Assert.Equal(2, Bitboard.PopCount(wpAttacks));
        Assert.True(Bitboard.Test(wpAttacks, Square.D5));
        Assert.True(Bitboard.Test(wpAttacks, Square.F5));

        ulong bpAttacks = Bitboard.PawnAttacks(Square.E4, Color.Black);
        Assert.Equal(2, Bitboard.PopCount(bpAttacks));
        Assert.True(Bitboard.Test(bpAttacks, Square.D3));
        Assert.True(Bitboard.Test(bpAttacks, Square.F3));

        // Edge pawn attacks
        ulong edgeWp = Bitboard.PawnAttacks(Square.A2, Color.White);
        Assert.Equal(1, Bitboard.PopCount(edgeWp));
        Assert.True(Bitboard.Test(edgeWp, Square.B3));
    }

    [Fact]
    public void Between_And_Line_Precomputations()
    {
        ulong between = Bitboard.Between[(int)Square.A1, (int)Square.A4];
        Assert.True(Bitboard.Test(between, Square.A2));
        Assert.True(Bitboard.Test(between, Square.A3));
        Assert.False(Bitboard.Test(between, Square.A1));
        Assert.False(Bitboard.Test(between, Square.A4));

        ulong line = Bitboard.Line[(int)Square.A1, (int)Square.A4];
        Assert.Equal(Bitboard.FileA, line);

        ulong diagBetween = Bitboard.Between[(int)Square.C1, (int)Square.F4];
        Assert.True(Bitboard.Test(diagBetween, Square.D2));
        Assert.True(Bitboard.Test(diagBetween, Square.E3));
        Assert.Equal(2, Bitboard.PopCount(diagBetween));
    }
}
