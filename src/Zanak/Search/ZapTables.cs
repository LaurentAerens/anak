using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Zanak.Search;

/// <summary>
/// Precomputed static bitboard, distance, material, and move-ordering lookup tables
/// initialized once at startup for zero-allocation Zanak spine probing and move ordering.
/// </summary>
public static class ZapTables
{
    /// <summary>Bitboard masks for ranks 0..7 (Rank 1 through Rank 8).</summary>
    public static readonly ulong[] RankMasks = new ulong[8];

    /// <summary>Bitboard masks for files 0..7 (File A through File H).</summary>
    public static readonly ulong[] FileMasks = new ulong[8];

    /// <summary>
    /// Rank masks indexed by <c>((int)color * 8) + relativeRank</c>, where relative rank 0 is the side's
    /// home rank (Rank 1 for White, Rank 8 for Black) and relative rank 7 is the promotion rank.
    /// </summary>
    public static readonly ulong[] RelativeRankMasks = new ulong[16];

    /// <summary>
    /// Piece values in centipawns indexed by <see cref="PieceType"/> (0..6),
    /// matching <see cref="StaticExchangeEvaluator"/>.
    /// </summary>
    public static readonly int[] PieceValues = [0, 100, 320, 330, 500, 900, 20000];

    /// <summary>
    /// Flat 7x7 MVV-LVA capture ordering table indexed by <c>((int)victim * 7) + (int)attacker</c>.
    /// </summary>
    public static readonly int[] MvvLvaScores = new int[7 * 7];

    /// <summary>
    /// King-ring masks for each square <c>0..63</c>: all squares within Chebyshev distance &lt;= 1,
    /// excluding the square itself.
    /// </summary>
    public static readonly ulong[] KingRingMasks = new ulong[64];

    /// <summary>
    /// Flat 64x64 Chebyshev (king-step) distance table indexed by <c>(sq1 * 64) + sq2</c>.
    /// </summary>
    public static readonly byte[] ChebyshevDistance = new byte[64 * 64];

    /// <summary>
    /// Flat 64x64 Manhattan (taxicab) distance table indexed by <c>(sq1 * 64) + sq2</c>.
    /// </summary>
    public static readonly byte[] ManhattanDistance = new byte[64 * 64];

    /// <summary>
    /// Forward pawn span masks indexed by <c>((int)color * 64) + (int)sq</c>: all squares strictly ahead
    /// of a pawn on <c>sq</c> along its own file and adjacent files.
    /// </summary>
    public static readonly ulong[] PawnForwardSpanMasks = new ulong[2 * 64];

    static ZapTables()
    {
        for (int rank = 0; rank < 8; rank++)
        {
            RankMasks[rank] = 0xFFUL << (rank * 8);
        }

        for (int file = 0; file < 8; file++)
        {
            FileMasks[file] = 0x0101010101010101UL << file;
        }

        for (int relRank = 0; relRank < 8; relRank++)
        {
            RelativeRankMasks[relRank] = RankMasks[relRank];
            RelativeRankMasks[8 + relRank] = RankMasks[7 - relRank];
        }

        for (int victim = 1; victim <= 6; victim++)
        {
            int victimValue = PieceValues[victim];
            for (int attacker = 1; attacker <= 6; attacker++)
            {
                // Cap King attacker penalty so legal King captures remain positively ordered below Queen captures of the same victim.
                int attackerPenalty = attacker == (int)PieceType.King ? 1000 : PieceValues[attacker];
                MvvLvaScores[(victim * 7) + attacker] = (victimValue * 100) - attackerPenalty;
            }
        }

        for (int sq1 = 0; sq1 < 64; sq1++)
        {
            int f1 = sq1 & 7;
            int r1 = sq1 >> 3;
            ulong ring = 0UL;

            for (int sq2 = 0; sq2 < 64; sq2++)
            {
                int f2 = sq2 & 7;
                int r2 = sq2 >> 3;
                int df = Math.Abs(f1 - f2);
                int dr = Math.Abs(r1 - r2);
                int cheb = Math.Max(df, dr);
                int manh = df + dr;

                int pairIdx = (sq1 << 6) | sq2;
                ChebyshevDistance[pairIdx] = (byte)cheb;
                ManhattanDistance[pairIdx] = (byte)manh;

                if (cheb == 1)
                {
                    ring |= 1UL << sq2;
                }
            }

            KingRingMasks[sq1] = ring;

            ulong fileSpan = FileMasks[f1];
            if (f1 > 0) fileSpan |= FileMasks[f1 - 1];
            if (f1 < 7) fileSpan |= FileMasks[f1 + 1];

            ulong whiteAhead = 0UL;
            for (int r = r1 + 1; r < 8; r++)
            {
                whiteAhead |= RankMasks[r];
            }
            PawnForwardSpanMasks[sq1] = fileSpan & whiteAhead;

            ulong blackAhead = 0UL;
            for (int r = r1 - 1; r >= 0; r--)
            {
                blackAhead |= RankMasks[r];
            }
            PawnForwardSpanMasks[64 + sq1] = fileSpan & blackAhead;
        }
    }

    /// <summary>
    /// Ensures static lookup tables are initialized at module load or before first search.
    /// </summary>
    [ModuleInitializer]
    internal static void EnsureInitialized() =>
        RuntimeHelpers.RunClassConstructor(typeof(ZapTables).TypeHandle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetRelativeRankMask(Color color, int relRank) =>
        (uint)color <= 1 && (uint)relRank < 8
            ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(RelativeRankMasks), ((int)color << 3) | relRank)
            : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPieceValue(PieceType pieceType) =>
        (uint)pieceType <= 6
            ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(PieceValues), (int)pieceType)
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetMvvLvaScore(PieceType victim, PieceType attacker) =>
        (uint)victim <= 6 && (uint)attacker <= 6
            ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(MvvLvaScores), ((int)victim * 7) + (int)attacker)
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetKingRingMask(Square sq) =>
        (uint)sq < 64
            ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(KingRingMasks), (int)sq)
            : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetChebyshevDistance(Square sq1, Square sq2) =>
        (uint)sq1 < 64 && (uint)sq2 < 64
            ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ChebyshevDistance), ((int)sq1 << 6) | (int)sq2)
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetManhattanDistance(Square sq1, Square sq2) =>
        (uint)sq1 < 64 && (uint)sq2 < 64
            ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ManhattanDistance), ((int)sq1 << 6) | (int)sq2)
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetPawnForwardSpanMask(Color color, Square sq) =>
        (uint)color <= 1 && (uint)sq < 64
            ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(PawnForwardSpanMasks), ((int)color << 6) | (int)sq)
            : 0UL;
}
