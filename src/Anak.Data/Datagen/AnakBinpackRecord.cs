using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Data.Datagen;

/// <summary>
/// Contiguous 144-byte binary record layout for zero-copy memory-mapped NNUE training.
/// Exact memory layout:
/// - our_indices: 32 x ushort (64 bytes)
/// - their_indices: 32 x ushort (64 bytes)
/// - target_eval: float (4 bytes)
/// - target_wdl: float (4 bytes)
/// - openness: float (4 bytes)
/// - piece_count: byte (1 byte)
/// - flags: byte (1 byte) [bit 0: is_volatile/blunder, bit 1: has_queens, bit 2: in_check]
/// - reserved: ushort (2 bytes)
/// Total: 144 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 144)]
public unsafe struct AnakBinpackRecord
{
    public const ushort SentinelIndex = 0xFFFF; // 65535 padding

    public fixed ushort OurIndices[32];
    public fixed ushort TheirIndices[32];
    public float TargetEval;
    public float TargetWdl;
    public float Openness;
    public byte PieceCount;
    public byte Flags;
    public ushort Reserved;

    /// <summary>
    /// Encodes a chess <see cref="Board"/> position into a 144-byte binpack record.
    /// </summary>
    /// <param name="board">Board position.</param>
    /// <param name="targetEval">Evaluation in centipawns or normalized score from search.</param>
    /// <param name="targetWdl">Game outcome from side-to-move perspective (1.0 = Win, 0.5 = Draw, 0.0 = Loss).</param>
    /// <param name="isVolatile">Whether the position represents a tactical swing / blunder (score swing >= 150 cp).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AnakBinpackRecord FromBoard(in Board board, float targetEval, float targetWdl, bool isVolatile = false)
    {
        var record = new AnakBinpackRecord
        {
            TargetEval = targetEval,
            TargetWdl = targetWdl,
            Openness = PawnTopology.CalculateOpenness(in board),
            PieceCount = (byte)BitOperations.PopCount(board.Occupied),
            Reserved = 0
        };

        byte flags = 0;
        if (isVolatile) flags |= 0x01;
        if (board.Queens != 0) flags |= 0x02;
        if (board.IsInCheck(board.SideToMove)) flags |= 0x04;
        record.Flags = flags;

        // Initialize indices to sentinel padding
        for (int i = 0; i < 32; i++)
        {
            record.OurIndices[i] = SentinelIndex;
            record.TheirIndices[i] = SentinelIndex;
        }

        bool isWhiteTurn = board.SideToMove == Color.White;
        int idx = 0;

        ulong occupied = board.Occupied;
        while (occupied != 0 && idx < 32)
        {
            Square sq = Bitboard.PopLsb(ref occupied);
            Piece piece = board.GetPieceAt(sq);
            if (piece == Piece.None) continue;

            int pIdx = (int)piece.Type() - 1; // 0..5 (P=0, N=1, B=2, R=3, Q=4, K=5)
            int cIdx = piece.Color() == Color.White ? 0 : 1;
            int sqVal = (int)sq;

            // White perspective:
            ushort whiteFeat = (ushort)(((cIdx * 6) + pIdx) * 64 + sqVal);
            // Black perspective (mirrored rank sq ^ 56, inverted color):
            ushort blackFeat = (ushort)((((1 - cIdx) * 6) + pIdx) * 64 + (sqVal ^ 56));

            if (isWhiteTurn)
            {
                record.OurIndices[idx] = whiteFeat;
                record.TheirIndices[idx] = blackFeat;
            }
            else
            {
                record.OurIndices[idx] = blackFeat;
                record.TheirIndices[idx] = whiteFeat;
            }

            idx++;
        }

        return record;
    }
}
