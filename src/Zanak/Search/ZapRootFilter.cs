using System.Runtime.CompilerServices;
using Anak.Core;
using Anak.Search;

namespace Zanak.Search;

/// <summary>
/// Cheap root pre-filter that removes clearly bad 1-ply moves BEFORE spine generation.
/// Uses a null move as the baseline: a root move is only discarded if it is clearly worse than
/// passing (doing nothing). Everything subtle (blunders deeper than 1 ply) is left to the agents.
/// Cost: one capture-generation per root move plus one for the null-move baseline.
/// </summary>
public static class ZapRootFilter
{
    /// <summary>
    /// Fills <paramref name="keep"/> (same indexing as <paramref name="moves"/>).
    /// Never rejects every move; never rejects checks or promotions.
    /// </summary>
    /// <returns>Number of moves rejected.</returns>
    public static int Filter(in Board root, ReadOnlySpan<Move> moves, Span<bool> keep, int marginCp)
    {
        for (int i = 0; i < moves.Length; i++) keep[i] = true;
        if (marginCp <= 0 || moves.Length <= 1 || root.IsInCheck()) return 0;

        // Null-move baseline: what can the opponent win if we simply pass?
        int nullLoss = 0;
        bool nullAllowsMate = false;
        Board nullBoard = root;
        if (nullBoard.MakeNullMove(out _))
        {
            nullLoss = MaxCaptureLoss(in nullBoard, Move.Null, in root);
            nullAllowsMate = ZapPolicy.TryGetImmediateMateIn1(in nullBoard, out _);
        }

        // Best winning capture available at root (if a free minor/major piece is hanging,
        // quiet moves that ignore it when nothing larger is hanging on our side are filtered).
        int bestRootCaptureSee = 0;
        for (int i = 0; i < moves.Length; i++)
        {
            if (moves[i].IsCapture)
            {
                int capSee = StaticExchangeEvaluator.Evaluate(in root, moves[i]);
                if (capSee > bestRootCaptureSee) bestRootCaptureSee = capSee;
            }
        }
        bool mustAddressFreeCapture = !nullAllowsMate && bestRootCaptureSee >= 200 && nullLoss < bestRootCaptureSee;

        Span<int> loss = stackalloc int[moves.Length];
        Span<bool> mated = stackalloc bool[moves.Length];
        int rejected = 0;

        for (int i = 0; i < moves.Length; i++)
        {
            Move m = moves[i];
            if (m.IsPromotion)
            {
                continue; // promotions: never filtered here, agents judge them
            }

            if (mustAddressFreeCapture && !m.IsCapture && !root.GivesCheck(m))
            {
                keep[i] = false;
                rejected++;
                continue;
            }

            Board after = root;
            if (!after.MakeMove(m, out _)) continue;

            if (root.GivesCheck(m))
            {
                // Never filter checkmate or reasonable tactical sacrifices (e.g. Bxh7+);
                // only reject blatant material-dump checks (e.g. Qxf2+ onto defended squares).
                MoveList replies = default;
                MoveGenerator.GenerateLegalMoves(in after, ref replies);
                if (replies.Count == 0)
                {
                    continue;
                }

                int checkSee = StaticExchangeEvaluator.Evaluate(in root, m);
                if (checkSee <= -marginCp * 2)
                {
                    keep[i] = false;
                    rejected++;
                }
                continue;
            }

            // Direct SEE safety check for the moved piece
            int directSeeLoss = 0;
            if (m.IsCapture)
            {
                // For a capture, see if the piece sacrificed on that square loses material net of what it captured
                int seeOnDestination = StaticExchangeEvaluator.Evaluate(in root, m);
                if (seeOnDestination < -marginCp / 2)
                {
                    directSeeLoss = -seeOnDestination;
                }
            }
            else
            {
                // For a quiet move, see if the moved piece hangs on its destination square
                int seeOnDestination = StaticExchangeEvaluator.Evaluate(in root, m);
                if (seeOnDestination < 0)
                {
                    directSeeLoss = -seeOnDestination;
                }
            }

            loss[i] = Math.Max(directSeeLoss, MaxCaptureLoss(in after, m, in root));
            if (!nullAllowsMate && ZapPolicy.AllowsMateIn1(in root, m)) mated[i] = true;

            bool unansweredThreat = nullLoss >= 200 && !m.IsCapture && loss[i] >= nullLoss;
            if (mated[i] || unansweredThreat || loss[i] >= nullLoss + marginCp || directSeeLoss >= marginCp / 2)
            {
                keep[i] = false;
                rejected++;
            }
        }

        if (rejected == moves.Length)
        {
            for (int i = 0; i < moves.Length; i++) keep[i] = true;
            return 0;
        }
        return rejected;
    }

    /// <summary>
    /// Largest net material the side to move in <paramref name="board"/> wins by a single capture (SEE),
    /// crediting back what <paramref name="rootMove"/> itself captured on the same square.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int MaxCaptureLoss(in Board board, Move rootMove, in Board rootBoard)
    {
        MoveList captures = default;
        MoveGenerator.GenerateLegalCaptures(in board, ref captures);

        int best = 0;
        for (int i = 0; i < captures.Count; i++)
        {
            Move reply = captures[i];
            int see = StaticExchangeEvaluator.Evaluate(in board, reply);

            if (!rootMove.IsNull && rootMove.IsCapture && reply.To == rootMove.To)
            {
                Piece ourVictim = rootMove.Type == MoveType.EnPassant
                    ? PieceExtensions.Create(rootBoard.SideToMove.Opponent(), PieceType.Pawn)
                    : rootBoard.GetPieceAt(rootMove.To);
                if (ourVictim != Piece.None)
                {
                    see -= StaticExchangeEvaluator.GetPieceValue(ourVictim.Type());
                }
            }

            if (see > best) best = see;
        }
        return best;
    }
}
