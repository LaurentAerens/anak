using System.Numerics;
using System.Runtime.CompilerServices;
using Anak.Core;

namespace Anak.Data.Evaluation;

/// <summary>
/// High-performance, zero-allocation default heuristic evaluator implementing <see cref="IEvaluator"/>.
/// Computes material balances, tapered piece-square tables (midgame/endgame),
/// mobility, pawn structure (doubled, isolated, passed), and king safety.
/// Returns value relative to the side to move.
/// </summary>
public sealed class DefaultEvaluator : IEvaluator
{
    // TODO: Custom neural/heuristic eval here (e.g. NNUE / ONNX tensor evaluations)

    // Base piece material values
    private const int PawnValue = 100;
    private const int KnightValue = 320;
    private const int BishopValue = 330;
    private const int RookValue = 500;
    private const int QueenValue = 900;
    private const int KingValue = 20000;

    // Game phase weights for tapered evaluation (Total = 24)
    private const int KnightPhase = 1;
    private const int BishopPhase = 1;
    private const int RookPhase = 2;
    private const int QueenPhase = 4;
    private const int TotalPhase = 24;

    #region Piece-Square Tables (Midgame & Endgame)

    // Midgame Pawn PST
    private static readonly int[] _pawnPstMg =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
          5,  10,  10, -20, -20,  10,  10,   5,
          5,  -5, -10,   0,   0, -10,  -5,   5,
          0,   0,   0,  20,  20,   0,   0,   0,
          5,   5,  10,  25,  25,  10,   5,   5,
         10,  10,  20,  30,  30,  20,  10,  10,
         50,  50,  50,  50,  50,  50,  50,  50,
          0,   0,   0,   0,   0,   0,   0,   0
    ];

    // Endgame Pawn PST
    private static readonly int[] _pawnPstEg =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   0,   0,
         10,  10,  10,  10,  10,  10,  10,  10,
         20,  20,  20,  20,  20,  20,  20,  20,
         35,  35,  35,  35,  35,  35,  35,  35,
         60,  60,  60,  60,  60,  60,  60,  60,
         90,  90,  90,  90,  90,  90,  90,  90,
          0,   0,   0,   0,   0,   0,   0,   0
    ];

    // Midgame Knight PST
    private static readonly int[] _knightPstMg =
    [
        -50, -40, -30, -30, -30, -30, -40, -50,
        -40, -20,   0,   5,   5,   0, -20, -40,
        -30,   5,  10,  15,  15,  10,   5, -30,
        -30,   0,  15,  20,  20,  15,   0, -30,
        -30,   5,  15,  20,  20,  15,   5, -30,
        -30,   0,  10,  15,  15,  10,   0, -30,
        -40, -20,   0,   0,   0,   0, -20, -40,
        -50, -40, -30, -30, -30, -30, -40, -50
    ];

    // Endgame Knight PST
    private static readonly int[] _knightPstEg =
    [
        -20, -10, -10, -10, -10, -10, -10, -20,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -10,   0,  10,  10,  10,  10,   0, -10,
        -10,   0,  10,  15,  15,  10,   0, -10,
        -10,   0,  10,  15,  15,  10,   0, -10,
        -10,   0,  10,  10,  10,  10,   0, -10,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -20, -10, -10, -10, -10, -10, -10, -20
    ];

    // Midgame Bishop PST
    private static readonly int[] _bishopPstMg =
    [
        -20, -10, -10, -10, -10, -10, -10, -20,
        -10,   5,   0,   0,   0,   0,   5, -10,
        -10,  10,  10,  10,  10,  10,  10, -10,
        -10,   0,  10,  10,  10,  10,   0, -10,
        -10,   5,   5,  10,  10,   5,   5, -10,
        -10,   0,   5,  10,  10,   5,   0, -10,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -20, -10, -10, -10, -10, -10, -10, -20
    ];

    // Endgame Bishop PST
    private static readonly int[] _bishopPstEg =
    [
        -10,  -5,  -5,  -5,  -5,  -5,  -5, -10,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   5,   5,   5,   5,   0,  -5,
         -5,   0,   5,  10,  10,   5,   0,  -5,
         -5,   0,   5,  10,  10,   5,   0,  -5,
         -5,   0,   5,   5,   5,   5,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
        -10,  -5,  -5,  -5,  -5,  -5,  -5, -10
    ];

    // Midgame Rook PST
    private static readonly int[] _rookPstMg =
    [
          0,   0,   0,   5,   5,   0,   0,   0,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
          5,  10,  10,  10,  10,  10,  10,   5,
          0,   0,   0,   0,   0,   0,   0,   0
    ];

    // Endgame Rook PST
    private static readonly int[] _rookPstEg =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
          5,   5,   5,   5,   5,   5,   5,   5,
          5,   5,   5,   5,   5,   5,   5,   5,
          5,   5,   5,   5,   5,   5,   5,   5,
          5,   5,   5,   5,   5,   5,   5,   5,
          5,   5,   5,   5,   5,   5,   5,   5,
         10,  10,  10,  10,  10,  10,  10,  10,
          0,   0,   0,   0,   0,   0,   0,   0
    ];

    // Midgame Queen PST
    private static readonly int[] _queenPstMg =
    [
        -20, -10, -10,  -5,  -5, -10, -10, -20,
        -10,   0,   5,   0,   0,   0,   0, -10,
        -10,   5,   5,   5,   5,   5,   0, -10,
          0,   0,   5,   5,   5,   5,   0,  -5,
         -5,   0,   5,   5,   5,   5,   0,  -5,
        -10,   0,   5,   5,   5,   5,   0, -10,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -20, -10, -10,  -5,  -5, -10, -10, -20
    ];

    // Endgame Queen PST
    private static readonly int[] _queenPstEg =
    [
        -10,  -5,  -5,  -5,  -5,  -5,  -5, -10,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   5,   5,   5,   5,   0,  -5,
         -5,   0,   5,  10,  10,   5,   0,  -5,
         -5,   0,   5,  10,  10,   5,   0,  -5,
         -5,   0,   5,   5,   5,   5,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
        -10,  -5,  -5,  -5,  -5,  -5,  -5, -10
    ];

    // Midgame King PST (Favors castling and shelter)
    private static readonly int[] _kingPstMg =
    [
         20,  30,  10,   0,   0,  10,  30,  20,
         20,  20, -10, -30, -30, -10,  20,  20,
        -10, -20, -30, -40, -40, -30, -20, -10,
        -20, -30, -40, -50, -50, -40, -30, -20,
        -30, -40, -50, -60, -60, -50, -40, -30,
        -30, -40, -50, -60, -60, -50, -40, -30,
        -30, -40, -50, -60, -60, -50, -40, -30,
        -30, -40, -50, -60, -60, -50, -40, -30
    ];

    // Endgame King PST (Favors active king in center)
    private static readonly int[] _kingPstEg =
    [
        -50, -30, -30, -30, -30, -30, -30, -50,
        -30, -10,   0,   0,   0,   0, -10, -30,
        -30,   0,  15,  20,  20,  15,   0, -30,
        -30,   0,  20,  30,  30,  20,   0, -30,
        -30,   0,  20,  30,  30,  20,   0, -30,
        -30,   0,  15,  20,  20,  15,   0, -30,
        -30, -10,   0,   0,   0,   0, -10, -30,
        -50, -30, -30, -30, -30, -30, -30, -50
    ];

    #endregion

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves)
    {
        // 1. Piece counts
        int whitePawnsCount = BitOperations.PopCount(board.Pawns & board.White);
        int blackPawnsCount = BitOperations.PopCount(board.Pawns & board.Black);
        int whiteKnightsCount = BitOperations.PopCount(board.Knights & board.White);
        int blackKnightsCount = BitOperations.PopCount(board.Knights & board.Black);
        int whiteBishopsCount = BitOperations.PopCount(board.Bishops & board.White);
        int blackBishopsCount = BitOperations.PopCount(board.Bishops & board.Black);
        int whiteRooksCount = BitOperations.PopCount(board.Rooks & board.White);
        int blackRooksCount = BitOperations.PopCount(board.Rooks & board.Black);
        int whiteQueensCount = BitOperations.PopCount(board.Queens & board.White);
        int blackQueensCount = BitOperations.PopCount(board.Queens & board.Black);

        // 2. Material balances
        int whiteMaterial = whitePawnsCount * PawnValue +
                            whiteKnightsCount * KnightValue +
                            whiteBishopsCount * BishopValue +
                            whiteRooksCount * RookValue +
                            whiteQueensCount * QueenValue;

        int blackMaterial = blackPawnsCount * PawnValue +
                            blackKnightsCount * KnightValue +
                            blackBishopsCount * BishopValue +
                            blackRooksCount * RookValue +
                            blackQueensCount * QueenValue;

        // 3. Game phase calculation for tapered evaluation
        int phase = (whiteKnightsCount + blackKnightsCount) * KnightPhase +
                    (whiteBishopsCount + blackBishopsCount) * BishopPhase +
                    (whiteRooksCount + blackRooksCount) * RookPhase +
                    (whiteQueensCount + blackQueensCount) * QueenPhase;

        phase = Math.Clamp(phase, 0, TotalPhase);
        int mgWeight = phase;
        int egWeight = TotalPhase - phase;

        // 4. Piece-Square Tables (Midgame & Endgame)
        int whitePstMg = 0, whitePstEg = 0;
        int blackPstMg = 0, blackPstEg = 0;

        EvaluatePst(board.Pawns & board.White, _pawnPstMg, _pawnPstEg, isWhite: true, ref whitePstMg, ref whitePstEg);
        EvaluatePst(board.Pawns & board.Black, _pawnPstMg, _pawnPstEg, isWhite: false, ref blackPstMg, ref blackPstEg);

        EvaluatePst(board.Knights & board.White, _knightPstMg, _knightPstEg, isWhite: true, ref whitePstMg, ref whitePstEg);
        EvaluatePst(board.Knights & board.Black, _knightPstMg, _knightPstEg, isWhite: false, ref blackPstMg, ref blackPstEg);

        EvaluatePst(board.Bishops & board.White, _bishopPstMg, _bishopPstEg, isWhite: true, ref whitePstMg, ref whitePstEg);
        EvaluatePst(board.Bishops & board.Black, _bishopPstMg, _bishopPstEg, isWhite: false, ref blackPstMg, ref blackPstEg);

        EvaluatePst(board.Rooks & board.White, _rookPstMg, _rookPstEg, isWhite: true, ref whitePstMg, ref whitePstEg);
        EvaluatePst(board.Rooks & board.Black, _rookPstMg, _rookPstEg, isWhite: false, ref blackPstMg, ref blackPstEg);

        EvaluatePst(board.Queens & board.White, _queenPstMg, _queenPstEg, isWhite: true, ref whitePstMg, ref whitePstEg);
        EvaluatePst(board.Queens & board.Black, _queenPstMg, _queenPstEg, isWhite: false, ref blackPstMg, ref blackPstEg);

        EvaluatePst(board.Kings & board.White, _kingPstMg, _kingPstEg, isWhite: true, ref whitePstMg, ref whitePstEg);
        EvaluatePst(board.Kings & board.Black, _kingPstMg, _kingPstEg, isWhite: false, ref blackPstMg, ref blackPstEg);

        // 5. Pawn structure (doubled, isolated, passed)
        int whitePawnStructure = EvaluatePawnStructure(board.Pawns & board.White, board.Pawns & board.Black, isWhite: true);
        int blackPawnStructure = EvaluatePawnStructure(board.Pawns & board.Black, board.Pawns & board.White, isWhite: false);

        // 6. King safety (pawn shelter, open files)
        int whiteKingSafety = EvaluateKingSafety(in board, Color.White);
        int blackKingSafety = EvaluateKingSafety(in board, Color.Black);

        // 7. Combine Midgame and Endgame scores
        int whiteMg = whiteMaterial + whitePstMg + whitePawnStructure + whiteKingSafety;
        int whiteEg = whiteMaterial + whitePstEg + whitePawnStructure;

        int blackMg = blackMaterial + blackPstMg + blackPawnStructure + blackKingSafety;
        int blackEg = blackMaterial + blackPstEg + blackPawnStructure;

        int mgScore = whiteMg - blackMg;
        int egScore = whiteEg - blackEg;
        int taperedScore = (mgScore * mgWeight + egScore * egWeight) / TotalPhase;

        // 8. Mobility bonus for side to move (centered around baseline)
        int mobilityBonus = (legalMoves.Length - 20) * 2;

        // 9. Side-to-move perspective
        int finalScore = board.SideToMove == Color.White
            ? taperedScore + mobilityBonus
            : -taperedScore + mobilityBonus;

        return new Value(finalScore);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EvaluatePst(ulong bitboard, int[] pstMg, int[] pstEg, bool isWhite, ref int totalMg, ref int totalEg)
    {
        while (bitboard != 0)
        {
            Square sq = Bitboard.PopLsb(ref bitboard);
            int idx = isWhite ? (int)sq : ((int)sq ^ 56);
            totalMg += pstMg[idx];
            totalEg += pstEg[idx];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EvaluatePawnStructure(ulong myPawns, ulong enemyPawns, bool isWhite)
    {
        int score = 0;

        for (int file = 0; file < 8; file++)
        {
            ulong filePawns = myPawns & Bitboard.FileMasks[file];
            int count = BitOperations.PopCount(filePawns);

            // Doubled pawns penalty
            if (count > 1)
            {
                score -= (count - 1) * 15;
            }

            // Isolated pawn penalty
            if (count > 0)
            {
                ulong adjMask = 0UL;
                if (file > 0) adjMask |= Bitboard.FileMasks[file - 1];
                if (file < 7) adjMask |= Bitboard.FileMasks[file + 1];

                if ((myPawns & adjMask) == 0)
                {
                    score -= count * 20;
                }
            }
        }

        // Passed pawns bonus
        ulong tempPawns = myPawns;
        while (tempPawns != 0)
        {
            Square sq = Bitboard.PopLsb(ref tempPawns);
            int file = (int)sq.File();
            int rank = (int)sq.Rank();

            ulong aheadMask = 0UL;
            if (isWhite)
            {
                for (int r = rank + 1; r < 8; r++)
                {
                    aheadMask |= Bitboard.RankMasks[r] & Bitboard.FileMasks[file];
                    if (file > 0) aheadMask |= Bitboard.RankMasks[r] & Bitboard.FileMasks[file - 1];
                    if (file < 7) aheadMask |= Bitboard.RankMasks[r] & Bitboard.FileMasks[file + 1];
                }

                if ((enemyPawns & aheadMask) == 0)
                {
                    score += rank switch
                    {
                        1 => 10,
                        2 => 15,
                        3 => 25,
                        4 => 45,
                        5 => 75,
                        6 => 120,
                        _ => 0
                    };
                }
            }
            else
            {
                for (int r = rank - 1; r >= 0; r--)
                {
                    aheadMask |= Bitboard.RankMasks[r] & Bitboard.FileMasks[file];
                    if (file > 0) aheadMask |= Bitboard.RankMasks[r] & Bitboard.FileMasks[file - 1];
                    if (file < 7) aheadMask |= Bitboard.RankMasks[r] & Bitboard.FileMasks[file + 1];
                }

                if ((enemyPawns & aheadMask) == 0)
                {
                    int advanced = 7 - rank;
                    score += advanced switch
                    {
                        1 => 10,
                        2 => 15,
                        3 => 25,
                        4 => 45,
                        5 => 75,
                        6 => 120,
                        _ => 0
                    };
                }
            }
        }

        return score;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EvaluateKingSafety(in Board board, Color color)
    {
        ulong kings = board.Kings & (color == Color.White ? board.White : board.Black);
        if (kings == 0) return 0;

        Square kingSq = Bitboard.Lsb(kings);
        int kingFile = (int)kingSq.File();
        int kingRank = (int)kingSq.Rank();

        ulong myPawns = board.Pawns & (color == Color.White ? board.White : board.Black);
        ulong allPawns = board.Pawns;
        int safetyScore = 0;

        // Castling rights & castled status evaluation
        if (color == Color.White)
        {
            if (kingSq is Square.G1 or Square.C1 or Square.B1)
            {
                safetyScore += 35; // Castled safely
            }
            else if ((board.Castling & CastlingRights.White) != 0)
            {
                safetyScore += 15; // Retains right to castle
            }
            else if (kingSq != Square.E1)
            {
                safetyScore -= 45; // Lost castling rights and king walked out
            }
        }
        else
        {
            if (kingSq is Square.G8 or Square.C8 or Square.B8)
            {
                safetyScore += 35; // Castled safely
            }
            else if ((board.Castling & CastlingRights.Black) != 0)
            {
                safetyScore += 15; // Retains right to castle
            }
            else if (kingSq != Square.E8)
            {
                safetyScore -= 45; // Lost castling rights and king walked out
            }
        }

        // Shelter files around king (only when king is on wing or castled)
        bool isWingKing = kingFile <= 2 || kingFile >= 5;
        if (isWingKing)
        {
            int minF = Math.Max(0, kingFile - 1);
            int maxF = Math.Min(7, kingFile + 1);

            for (int f = minF; f <= maxF; f++)
            {
                ulong fileMask = Bitboard.FileMasks[f];
                ulong myFilePawns = myPawns & fileMask;

                if (myFilePawns == 0)
                {
                    // Semi-open file penalty near king
                    safetyScore -= 20;

                    // Completely open file penalty
                    if ((allPawns & fileMask) == 0)
                    {
                        safetyScore -= 15;
                    }
                }
                else
                {
                    // Shelter bonus if pawn is close to king
                    if (color == Color.White && kingRank <= 1)
                    {
                        safetyScore += 15;
                    }
                    else if (color == Color.Black && kingRank >= 6)
                    {
                        safetyScore += 15;
                    }
                }
            }
        }
        else
        {
            // Exposed center king in midgame
            safetyScore -= 25;
        }

        return safetyScore;
    }
}
