using System.Numerics;
using System.Runtime.CompilerServices;

namespace Anak.Core;

public static class Bitboard
{
    public const ulong Empty = 0UL;
    public const ulong All = ~0UL;

    public const ulong Rank1 = 0x00000000000000FFUL;
    public const ulong Rank2 = 0x000000000000FF00UL;
    public const ulong Rank3 = 0x0000000000FF0000UL;
    public const ulong Rank4 = 0x00000000FF000000UL;
    public const ulong Rank5 = 0x000000FF00000000UL;
    public const ulong Rank6 = 0x0000FF0000000000UL;
    public const ulong Rank7 = 0x00FF000000000000UL;
    public const ulong Rank8 = 0xFF00000000000000UL;

    public const ulong FileA = 0x0101010101010101UL;
    public const ulong FileB = 0x0202020202020202UL;
    public const ulong FileC = 0x0404040404040404UL;
    public const ulong FileD = 0x0808080808080808UL;
    public const ulong FileE = 0x1010101010101010UL;
    public const ulong FileF = 0x2020202020202020UL;
    public const ulong FileG = 0x4040404040404040UL;
    public const ulong FileH = 0x8080808080808080UL;

    public static readonly ulong[] RankMasks =
    [
        Rank1, Rank2, Rank3, Rank4, Rank5, Rank6, Rank7, Rank8
    ];

    public static readonly ulong[] FileMasks =
    [
        FileA, FileB, FileC, FileD, FileE, FileF, FileG, FileH
    ];

    public static readonly ulong[] DiagonalMasks = new ulong[15];
    public static readonly ulong[] AntiDiagonalMasks = new ulong[15];

    public static readonly ulong[] SquareDiagonalMask = new ulong[64];
    public static readonly ulong[] SquareAntiDiagonalMask = new ulong[64];

    public static readonly ulong[] WhitePawnAttacks = new ulong[64];
    public static readonly ulong[] BlackPawnAttacks = new ulong[64];
    public static readonly ulong[] KnightAttacks = new ulong[64];
    public static readonly ulong[] KingAttacks = new ulong[64];

    public static readonly ulong[,] Between = new ulong[64, 64];
    public static readonly ulong[,] Line = new ulong[64, 64];

    static Bitboard()
    {
        // Diagonal masks: file - rank in [-7, 7], mapped to [0, 14] via + 7
        // AntiDiagonal masks: file + rank in [0, 14]
        for (int rank = 0; rank < 8; rank++)
        {
            for (int file = 0; file < 8; file++)
            {
                int sq = (rank << 3) | file;
                ulong bb = 1UL << sq;

                int diag = file - rank + 7;
                DiagonalMasks[diag] |= bb;
                SquareDiagonalMask[sq] = (ulong)diag;

                int antiDiag = file + rank;
                AntiDiagonalMasks[antiDiag] |= bb;
                SquareAntiDiagonalMask[sq] = (ulong)antiDiag;
            }
        }

        // Set the actual mask values for square lookup
        for (int sq = 0; sq < 64; sq++)
        {
            SquareDiagonalMask[sq] = DiagonalMasks[(int)SquareDiagonalMask[sq]];
            SquareAntiDiagonalMask[sq] = AntiDiagonalMasks[(int)SquareAntiDiagonalMask[sq]];
        }

        // Precompute Pawn, Knight, and King attacks
        for (int sq = 0; sq < 64; sq++)
        {
            int file = sq & 7;
            int rank = sq >> 3;

            // White pawn attacks
            ulong wpAttacks = 0UL;
            if (rank < 7)
            {
                if (file > 0) wpAttacks |= 1UL << (sq + 7);
                if (file < 7) wpAttacks |= 1UL << (sq + 9);
            }
            WhitePawnAttacks[sq] = wpAttacks;

            // Black pawn attacks
            ulong bpAttacks = 0UL;
            if (rank > 0)
            {
                if (file > 0) bpAttacks |= 1UL << (sq - 9);
                if (file < 7) bpAttacks |= 1UL << (sq - 7);
            }
            BlackPawnAttacks[sq] = bpAttacks;

            // Knight attacks
            ulong nAttacks = 0UL;
            (int df, int dr)[] knightOffsets = [
                (-2, -1), (-2, 1), (-1, -2), (-1, 2),
                (1, -2), (1, 2), (2, -1), (2, 1)
            ];
            foreach (var (df, dr) in knightOffsets)
            {
                int f = file + df;
                int r = rank + dr;
                if (f is >= 0 and < 8 && r is >= 0 and < 8)
                {
                    nAttacks |= 1UL << ((r << 3) | f);
                }
            }
            KnightAttacks[sq] = nAttacks;

            // King attacks
            ulong kAttacks = 0UL;
            for (int dr = -1; dr <= 1; dr++)
            {
                for (int df = -1; df <= 1; df++)
                {
                    if (df == 0 && dr == 0) continue;
                    int f = file + df;
                    int r = rank + dr;
                    if (f is >= 0 and < 8 && r is >= 0 and < 8)
                    {
                        kAttacks |= 1UL << ((r << 3) | f);
                    }
                }
            }
            KingAttacks[sq] = kAttacks;
        }

        // Precompute Between and Line arrays
        for (int sq1 = 0; sq1 < 64; sq1++)
        {
            int f1 = sq1 & 7;
            int r1 = sq1 >> 3;

            for (int sq2 = 0; sq2 < 64; sq2++)
            {
                if (sq1 == sq2) continue;

                int f2 = sq2 & 7;
                int r2 = sq2 >> 3;

                int df = f2 - f1;
                int dr = r2 - r1;

                int stepF = Math.Sign(df);
                int stepR = Math.Sign(dr);

                bool isRank = r1 == r2;
                bool isFile = f1 == f2;
                bool isDiag = Math.Abs(df) == Math.Abs(dr);

                if (isRank || isFile || isDiag)
                {
                    // Full line passing through sq1 and sq2
                    ulong lineBb = 0UL;
                    int curF = f1;
                    int curR = r1;
                    while (curF >= 0 && curF < 8 && curR >= 0 && curR < 8)
                    {
                        lineBb |= 1UL << ((curR << 3) | curF);
                        curF -= stepF;
                        curR -= stepR;
                    }
                    curF = f1 + stepF;
                    curR = r1 + stepR;
                    while (curF >= 0 && curF < 8 && curR >= 0 && curR < 8)
                    {
                        lineBb |= 1UL << ((curR << 3) | curF);
                        curF += stepF;
                        curR += stepR;
                    }
                    Line[sq1, sq2] = lineBb;

                    // Squares strictly between sq1 and sq2
                    ulong betweenBb = 0UL;
                    curF = f1 + stepF;
                    curR = r1 + stepR;
                    while (curF != f2 || curR != r2)
                    {
                        betweenBb |= 1UL << ((curR << 3) | curF);
                        curF += stepF;
                        curR += stepR;
                    }
                    Between[sq1, sq2] = betweenBb;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PopCount(ulong bb) => BitOperations.PopCount(bb);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TrailingZeroCount(ulong bb) => BitOperations.TrailingZeroCount(bb);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeadingZeroCount(ulong bb) => BitOperations.LeadingZeroCount(bb);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Square Lsb(ulong bb) => (Square)BitOperations.TrailingZeroCount(bb);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Square Msb(ulong bb) => (Square)(63 - BitOperations.LeadingZeroCount(bb));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong FromSquare(Square sq) => 1UL << (byte)sq;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong FromSquare(int sq) => 1UL << sq;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Set(ulong bb, Square sq) => bb | (1UL << (byte)sq);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Clear(ulong bb, Square sq) => bb & ~(1UL << (byte)sq);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Test(ulong bb, Square sq) => (bb & (1UL << (byte)sq)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Square PopLsb(ref ulong bb)
    {
        int sq = BitOperations.TrailingZeroCount(bb);
        bb &= bb - 1;
        return (Square)sq;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong PawnAttacks(Square sq, Color color) =>
        color == Color.White ? WhitePawnAttacks[(byte)sq] : BlackPawnAttacks[(byte)sq];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong WhitePawnEastAttacks(ulong pawns) => (pawns & ~FileH) << 9;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong WhitePawnWestAttacks(ulong pawns) => (pawns & ~FileA) << 7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BlackPawnEastAttacks(ulong pawns) => (pawns & ~FileH) >> 7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BlackPawnWestAttacks(ulong pawns) => (pawns & ~FileA) >> 9;
}
