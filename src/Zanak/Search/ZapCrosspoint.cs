using Anak.Core;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Zanak.Search;

/// <summary>
/// Classification of positions along a trajectory spine.
/// </summary>
public enum CrosspointKind : byte
{
    /// <summary>
    /// Engine's turn: an engine move dropped evaluation, suggesting greedy policy missed a stronger move.
    /// </summary>
    Opportunity = 0,

    /// <summary>
    /// Opponent's turn: an opponent move caused an evaluation surge for the engine, suggesting greedy policy blundered for opponent.
    /// </summary>
    AdversarialRefutation = 1
}

/// <summary>
/// Operational assignment role for asymmetric worker agents.
/// </summary>
public enum ZapAgentRole : byte
{
    /// <summary>
    /// Refutation squad: targets the Leader on opponent plies to stress-test and refute it.
    /// </summary>
    Refutation = 0,

    /// <summary>
    /// Promotion squad: targets qualified Contenders on engine plies to discover tactics that elevate them.
    /// </summary>
    Promotion = 1
}

/// <summary>
/// Descriptor representing a position eligible for worker exploration.
/// Deduplicated by ZobristKey with shared LinkedSpinesMask across multiple root spines.
/// </summary>
public struct ZapCrosspoint : IComparable<ZapCrosspoint>
{
    // Board carries an 8 KB repetition table, so it is boxed: subspine crosspoints leave it empty and
    // keep only their place in the spine tree, rebuilding the board when an agent picks them up.
    private StrongBox<Board>? _board;
    public Board Board
    {
        readonly get => _board != null ? _board.Value : default;
        set => _board = new StrongBox<Board>(value);
    }
    public readonly bool HasBoard => _board != null;
    internal readonly StrongBox<Board>? BoardBox => _board;
    internal void ShareBoard(StrongBox<Board>? box) => _board = box;
    internal ZapSpineTree.Edge? TreeEdge;
    internal int TreeStep;
    public short SpineGap;
    public ulong ZobristKey;
    public ulong LinkedSpinesMask;
    public uint Generation;
    public ZapAgentRole Role;
    public int Ply;
    public short BaselineScore;
    public short ParentEval;
    public short ShockMagnitude;
    public int TacticalPriority;
    public CrosspointKind Kind;
    public int RemainingHorizon;
    public Move OriginalMove;
    public int PriorityScore;
    public bool Picked;
    public byte ReopenCount;
    internal bool InFlight;
    internal int Revision;
    internal ZapSiblingWork? Work;

    // Backward-compatibility property: single RootCandidateIndex maps to the lowest bit in LinkedSpinesMask
    public int RootCandidateIndex
    {
        get
        {
            if (LinkedSpinesMask == 0) return 0;
            return System.Numerics.BitOperations.TrailingZeroCount(LinkedSpinesMask);
        }
    }

    public ZapCrosspoint(
        in Board board,
        int rootCandidateIndex,
        int ply,
        short baselineScore,
        short shockMagnitude,
        CrosspointKind kind,
        int remainingHorizon,
        Move originalMove,
        int rootBiasWeight = 25,
        uint generation = 1,
        ZapAgentRole role = ZapAgentRole.Refutation,
        short parentEval = 0,
        bool prevWasCapture = false,
        ZapParams? parameters = null)
    {
        Board = board;
        ZobristKey = board.ZobristKey;
        LinkedSpinesMask = (rootCandidateIndex >= 0 && rootCandidateIndex < 64) ? (1UL << rootCandidateIndex) : 1UL;
        Generation = generation;
        Role = role;
        Ply = ply;
        BaselineScore = baselineScore;
        ParentEval = parentEval;
        ShockMagnitude = shockMagnitude;
        TacticalPriority = GetTacticalPriority(in board, originalMove, ply, prevWasCapture, parameters);
        Kind = kind;
        RemainingHorizon = remainingHorizon;
        OriginalMove = originalMove;
        PriorityScore = shockMagnitude + TacticalPriority + (Math.Max(0, 32 - ply) * rootBiasWeight);
        Picked = false;
    }

    public ZapCrosspoint(
        in Board board,
        ulong linkedSpinesMask,
        int ply,
        short baselineScore,
        short parentEval,
        short shockMagnitude,
        CrosspointKind kind,
        int remainingHorizon,
        Move originalMove,
        int priorityScore,
        uint generation = 1,
        ZapAgentRole role = ZapAgentRole.Refutation,
        bool prevWasCapture = false,
        ZapParams? parameters = null)
    {
        Board = board;
        ZobristKey = board.ZobristKey;
        LinkedSpinesMask = linkedSpinesMask;
        Generation = generation;
        Role = role;
        Ply = ply;
        BaselineScore = baselineScore;
        ParentEval = parentEval;
        ShockMagnitude = shockMagnitude;
        TacticalPriority = GetTacticalPriority(in board, originalMove, ply, prevWasCapture, parameters);
        Kind = kind;
        RemainingHorizon = remainingHorizon;
        OriginalMove = originalMove;
        PriorityScore = priorityScore;
        Picked = false;
    }

    /// <summary>
    /// Subspine crosspoint: the position at <paramref name="step"/> of a tree edge. Tactical features are
    /// read from <paramref name="board"/> now; the board itself is rebuilt from the tree when picked.
    /// </summary>
    internal ZapCrosspoint(
        in Board board,
        ZapSpineTree.Edge edge,
        int step,
        ulong linkedSpinesMask,
        int ply,
        short baselineScore,
        short parentEval,
        short shockMagnitude,
        short spineGap,
        int remainingHorizon,
        Move originalMove,
        uint generation,
        ZapAgentRole role,
        bool prevWasCapture = false,
        ZapParams? parameters = null)
    {
        _board = null;
        TreeEdge = edge;
        TreeStep = step;
        SpineGap = spineGap;
        ZobristKey = board.ZobristKey;
        LinkedSpinesMask = linkedSpinesMask;
        Generation = generation;
        Role = role;
        Ply = ply;
        BaselineScore = baselineScore;
        ParentEval = parentEval;
        ShockMagnitude = shockMagnitude;
        TacticalPriority = GetTacticalPriority(in board, originalMove, ply, prevWasCapture, parameters);
        Kind = role == ZapAgentRole.Refutation ? CrosspointKind.AdversarialRefutation : CrosspointKind.Opportunity;
        RemainingHorizon = remainingHorizon;
        OriginalMove = originalMove;
        PriorityScore = shockMagnitude + TacticalPriority;
        Picked = false;
    }

    /// <summary>
    /// Higher priority score (higher shock and closer to root) has higher priority in work queue.
    /// </summary>
    public int CompareTo(ZapCrosspoint other) => other.PriorityScore.CompareTo(PriorityScore);

    private static int GetTacticalPriority(in Board board, Move move, int ply, bool prevWasCapture, ZapParams? parameters)
    {
        ZapParams resolvedParams = parameters ?? ZapParams.Default;
        int priority = GetMoveRiskPriority(in board, move, ply, prevWasCapture, resolvedParams);

        int exposure = 0;
        ulong pieces = board.Occupied & ~(board.Pawns | board.Kings);
        while (pieces != 0)
        {
            Square square = (Square)BitOperations.TrailingZeroCount(pieces);
            pieces &= pieces - 1;
            Piece victim = board.GetPieceAt(square);
            ulong enemies = victim.Color() == Color.White ? board.Black : board.White;
            ulong attackers = StaticExchangeEvaluator.GetAllAttackers(in board, square, board.Occupied) & enemies;
            if (attackers == 0) continue;

            int value = StaticExchangeEvaluator.GetPieceValue(victim.Type());
            if (!board.IsSquareAttacked(square, victim.Color()))
            {
                exposure = Math.Max(exposure, value);
                continue;
            }

            while (attackers != 0)
            {
                Square attacker = (Square)BitOperations.TrailingZeroCount(attackers);
                attackers &= attackers - 1;
                exposure = Math.Max(exposure, value - StaticExchangeEvaluator.GetPieceValue(board.GetPieceAt(attacker).Type()));
            }
        }

        int materialSignal = GetMaterialSignal(in board, move);
        if (ply == 1 && (exposure > 0 || materialSignal > 0))
        {
            priority += resolvedParams.CrosspointPly1CaptureThreatWeight;
        }

        // This is a scheduling hint, not proof that a capture is legal or wins material.
        return priority + Math.Max(Math.Min(900, exposure) / 2, Math.Min(900, materialSignal));
    }

    /// <summary>
    /// Scores crosspoint pickup priority according to where the 25-step greedy policy empirically
    /// drops Stockfish rank: check evasions, promotions, and SEE>0 captures rarely need alternatives,
    /// while quiet moves (especially pawn pushes, capture->quiet transitions, deep plies, and moves
    /// landing on attacked squares) carry the majority of rank drops and centipawn losses.
    /// </summary>
    private static int GetMoveRiskPriority(in Board board, Move move, int ply, bool prevWasCapture, ZapParams parameters)
    {
        if (move.IsNull || move.IsPromotion || board.IsInCheck()) return 0;

        if (move.IsCapture)
        {
            return StaticExchangeEvaluator.Evaluate(in board, move) <= 0
                ? parameters.CrosspointNonWinningCaptureBonus
                : 0;
        }

        int priority = parameters.CrosspointQuietBonus;
        if (board.GetPieceAt(move.From).Type() == PieceType.Pawn && !board.GivesCheck(move))
        {
            priority += parameters.CrosspointQuietPawnPushBonus;
        }
        if (prevWasCapture) priority += parameters.CrosspointCaptureToQuietBonus;
        if (ply >= 6) priority += parameters.CrosspointDeepQuietBonus;

        Board after = board;
        if (after.MakeMove(move, out _) && after.IsSquareAttacked(move.To, after.SideToMove))
        {
            priority += parameters.CrosspointAttackedDestBonus;
        }

        return priority;
    }

    /// <summary>
    /// How much material the spine move gives away at this decision: either it skips a winning capture
    /// (including a recapture) or it leaves something en prise for the reply. A pickup hint only.
    /// </summary>
    internal static int GetMaterialSignal(in Board board, Move move)
    {
        if (move.IsNull) return 0;
        int moveGain = move.IsCapture ? StaticExchangeEvaluator.Evaluate(in board, move) : 0;
        int missed = Math.Max(0, BestCaptureGain(in board) - Math.Max(0, moveGain));

        int taken = !move.IsCapture ? 0 : move.Type == MoveType.EnPassant
            ? StaticExchangeEvaluator.GetPieceValue(PieceType.Pawn)
            : StaticExchangeEvaluator.GetPieceValue(board.GetPieceAt(move.To).Type());
        Board after = board;
        if (!after.MakeMove(move, out _)) return missed;
        int leftHanging = Math.Max(0, BestCaptureGain(in after) - taken);
        return Math.Max(missed, leftHanging);
    }

    private static int BestCaptureGain(in Board board)
    {
        MoveList moves = default;
        // The capture-only generator does not restrict non-king replies to check evasions.
        if (board.IsInCheck()) MoveGenerator.GenerateLegalMoves(in board, ref moves);
        else MoveGenerator.GenerateLegalCaptures(in board, ref moves);
        int best = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            if (moves[i].IsCapture) best = Math.Max(best, StaticExchangeEvaluator.Evaluate(in board, moves[i]));
        }
        return best;
    }
}
