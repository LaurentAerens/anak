using Anak.Core;
using Xunit;
using Zanak.Search;

namespace Anak.Tests.Zanak;

public sealed class ZapWorkQueueTests
{
    [Fact]
    public void Responsibility_BlamesUnpaidReplyAndAllowingMove_NotTheCapture()
    {
        // Engine is White. 54 -> 53 (Black hangs a knight) -> 314 (White takes it) -> 354 (Black plays a silent
        // move instead of recapturing) -> 360 (White grabs a pawn badly) -> -100 (Black punishes it).
        ZapTrajectoryStep[] steps =
        [
            Step(Color.Black, 54, 53),
            Step(Color.White, 53, 314),
            Step(Color.Black, 314, 354),
            Step(Color.White, 354, 360),
            Step(Color.Black, 360, -100),
            Step(Color.White, -100, -105),
        ];

        int[] blame = ZapWorkQueue.ComputeResponsibility(steps, Color.White, 1, new ZapParams());

        Assert.Equal(0, blame[1]);
        Assert.Equal(0, blame[4]);
        Assert.True(blame[2] > blame[1], "the silent non-recapture must outrank the capture that exposed it");
        Assert.True(blame[0] > 0, "the move that hung the knight shares the blame");
        Assert.True(blame[3] > 0 && blame[5] > 0, "the bad grab and the unpaid reply after it carry the second swing");
        Assert.Equal(261, blame[2]);
    }

    [Fact]
    public void Responsibility_DetectsSlowThreePlyDrift_WithoutSinglePlyShock()
    {
        // Engine is White; each quiet ply drifts -25cp/-20cp/-25cp = -70cp over 3 plies
        // without any single ply crossing ShockThresholdCp (73) or QuietEvalDropMinCp (45).
        ZapTrajectoryStep[] steps =
        [
            Step(Color.White, 0, -25),
            Step(Color.Black, -25, -45),
            Step(Color.White, -45, -70),
        ];

        int[] blame = ZapWorkQueue.ComputeResponsibility(steps, Color.White, 1, new ZapParams());

        Assert.Equal(70, blame[0]);
    }

    private static ZapTrajectoryStep Step(Color side, short parent, short eval) =>
        new(default, 0, Move.Null, side, parent, eval, (short)(eval - parent));

    [Fact]
    public void WorkQueue_ImmediateRefutation_PrecedesModerateDeeperCrosspoint()
    {
        var queue = new ZapWorkQueue();
        Board board = Fen.Parse(Fen.StartPosition);

        queue.Enqueue(new ZapCrosspoint(board, 0, 3, 0, 100,
            CrosspointKind.AdversarialRefutation, 5, Move.Null));
        queue.Enqueue(new ZapCrosspoint(board, 1, 2, 0, 100,
            CrosspointKind.Opportunity, 5, Move.Null, role: ZapAgentRole.Promotion));
        queue.Enqueue(new ZapCrosspoint(board, 2, 1, 0, 0,
            CrosspointKind.AdversarialRefutation, 5, Move.Null));

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(1, first.Ply);
        Assert.Equal(2, first.RootCandidateIndex);
    }

    [Fact]
    public void WorkQueue_HighValueOrLargeShockDeeperCrosspoint_PreemptsLowValueImmediateRefutation()
    {
        var queue = new ZapWorkQueue();
        Board board = Fen.Parse(Fen.StartPosition);

        queue.Enqueue(new ZapCrosspoint(board, 0, 3, 30000, short.MaxValue,
            CrosspointKind.AdversarialRefutation, 5, Move.Null));
        queue.Enqueue(new ZapCrosspoint(board, 1, 2, 30000, short.MaxValue,
            CrosspointKind.Opportunity, 5, Move.Null, role: ZapAgentRole.Promotion));
        queue.Enqueue(new ZapCrosspoint(board, 2, 1, -1000, 0,
            CrosspointKind.AdversarialRefutation, 5, Move.Null));

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(first.Ply > 1, "large deeper shock on high-value spine should preempt low-value ply-1");
        Assert.NotEqual(2, first.RootCandidateIndex);
    }

    [Fact]
    public void WorkQueue_HighShockAbove_PenalizesDeeperCrosspoints()
    {
        Board board = Fen.Parse(Fen.StartPosition);
        Board b1 = board;
        var m1 = new Move(Square.E2, Square.E4);
        Assert.True(b1.MakeMove(m1, out _));
        Board b2 = b1;
        var m2 = new Move(Square.E7, Square.E5);
        Assert.True(b2.MakeMove(m2, out _));
        Board b3 = b2;
        var m3 = new Move(Square.G1, Square.F3);
        Assert.True(b3.MakeMove(m3, out _));

        // Spine with a 300cp blunder at ply 1 and a 60cp shock at ply 3.
        ZapTrajectoryStep[] noisySteps =
        [
            new(default, board.ZobristKey, m1, Color.Black, 0, 300, 300),
            new(default, b1.ZobristKey, m2, Color.White, 300, 300, 0),
            new(default, b2.ZobristKey, m3, Color.Black, 300, 360, 60),
        ];

        var queue = new ZapWorkQueue();
        queue.EnqueueTrajectory(in board, rootIndex: 0, noisySteps, Color.White, generation: 1, startingPly: 1);

        var ply3 = Assert.Single(queue.Snapshot(), cp => cp.Ply == 3);
        Assert.True(ply3.ShockMagnitude < 0, $"expected malus from ply-1 shock above, got {ply3.ShockMagnitude}");
    }

    [Fact]
    public void WorkQueue_ImmediateRefutation_YieldsStrictPrecedenceAfterFirstBatch()
    {
        var queue = new ZapWorkQueue();
        Board board = Fen.Parse(Fen.StartPosition);
        queue.Enqueue(new ZapCrosspoint(board, 0, 1, 0, 0,
            CrosspointKind.AdversarialRefutation, 5, Move.Null));
        queue.Enqueue(new ZapCrosspoint(board, 1, 3, 0, 200,
            CrosspointKind.AdversarialRefutation, 3, Move.Null));

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(1, first.Ply);
        var work = new ZapSiblingWork
        {
            Moves = [new Move(Square.E2, Square.E4), new Move(Square.D2, Square.D4)],
            Next = 1,
            Batches = 1
        };
        queue.FinishBatch(in first, work, pending: true);

        Assert.True(queue.TryDequeue(out var tactical));
        Assert.Equal(3, tactical.Ply);
        Assert.True(queue.TryDequeue(out var resumed));
        Assert.Equal(1, resumed.Ply);
        Assert.Same(work, resumed.Work);
    }

    [Fact]
    public void WorkQueue_LowParentEval_AndDeadRoots_RemainEligible()
    {
        var queue = new ZapWorkQueue();
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);
        sharedState.Reset([new Move(Square.E2, Square.E4)]);
        sharedState.UpdateCandidateBaseline(0, 2000);
        uint generation = sharedState.NextGeneration();

        queue.Enqueue(new ZapCrosspoint(board, 0, 1, 0, 0,
            CrosspointKind.AdversarialRefutation, 5, Move.Null,
            generation: generation, parentEval: -1000));
        queue.Enqueue(new ZapCrosspoint(board, 0, 3, 0, 100,
            CrosspointKind.AdversarialRefutation, 5, Move.Null,
            generation: generation, parentEval: -1000));

        Assert.True(queue.TryDequeue(out var immediate, sharedState, generation));
        Assert.Equal(1, immediate.Ply);
        Assert.True(queue.TryDequeue(out var deeper, sharedState, generation));
        Assert.Equal(3, deeper.Ply);

        queue.Enqueue(new ZapCrosspoint(board, 0, 5, 0, 100,
            CrosspointKind.AdversarialRefutation, 5, Move.Null,
            generation: generation, parentEval: -1000));

        sharedState.SetRootMoveStatus(0, RootMoveStatus.Dead);
        Assert.True(queue.TryDequeue(out var dead, sharedState, generation));
        Assert.Equal(5, dead.Ply);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void WorkQueue_Reconsider_ReopensExploredDecision_UpToCap()
    {
        var queue = new ZapWorkQueue(new ZapParams { CrosspointMaxReopens = 2 });
        Board board = Fen.Parse(Fen.StartPosition);
        var decision = new ZapCrosspoint(board, 0, 3, 0, 0, CrosspointKind.Opportunity, 5, Move.Null);
        queue.Enqueue(decision);
        Assert.True(queue.TryDequeue(out var first));
        queue.FinishBatch(in first, null, false);

        for (int reopen = 1; reopen <= 2; reopen++)
        {
            queue.Reconsider(in decision, 300);
            Assert.True(queue.TryDequeue(out var revisited));
            Assert.Equal(300 * reopen, revisited.ShockMagnitude);
            queue.FinishBatch(in revisited, null, false);
        }

        queue.Reconsider(in decision, 300);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void WorkQueue_DeeperRequestWaitsForInFlightBatch_AndDiscardsOldProgress()
    {
        var queue = new ZapWorkQueue();
        Board board = Fen.Parse(Fen.StartPosition);
        var cp = new ZapCrosspoint(board, 0, 3, 0, 0, CrosspointKind.Opportunity, 2, Move.Null);
        queue.Enqueue(in cp);
        Assert.True(queue.TryDequeue(out var leased));
        cp.RemainingHorizon = 5;
        queue.Enqueue(in cp);
        Assert.False(queue.TryDequeue(out _));
        Assert.False(queue.IsCurrent(in leased));

        queue.FinishBatch(in leased, new ZapSiblingWork { Moves = [new Move(Square.E2, Square.E4)] }, true);
        Assert.True(queue.TryDequeue(out var deeper));
        Assert.Equal(5, deeper.RemainingHorizon);
        Assert.Null(deeper.Work);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void WorkQueue_CostAwarePickupExplainsPriority_AndKeepsExpensiveWorkEligible()
    {
        var queue = new ZapWorkQueue(new ZapParams { CrosspointCostWeight = 10 });
        var traces = new List<ZapPickupTrace>();
        queue.Picked += traces.Add;
        Board board = Fen.Parse(Fen.StartPosition);
        var expensive = new ZapCrosspoint(board, 0, 3, 0, 0, CrosspointKind.Opportunity, 10, Move.Null);
        var cheap = new ZapCrosspoint(board, 0, 4, 0, 0, CrosspointKind.Opportunity, 1, Move.Null);
        queue.Enqueue(in expensive);
        queue.Enqueue(in cheap);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(4, first.Ply);
        ZapPickupTrace reason = Assert.Single(traces);
        Assert.Equal(reason.PriorityClass + reason.Responsibility + reason.Tactics + reason.Proximity
            - reason.SubspineGap - reason.EstimatedCost - reason.ProgressPenalty + reason.SpineScore
            - reason.Diversity, reason.Total);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(3, second.Ply);
    }

    [Fact]
    public void BackCredit_TargetsOtherSidesEarlierDecisions_WithDecay()
    {
        var boosts = new int[6];
        ZapWorkerPool.BackCreditTargets(6, 400, new ZapParams { BackCreditMaxSteps = 3, CrosspointDebtDecayPct = 50 }, boosts);
        Assert.Equal(new[] { 0, 100, 0, 200, 0, 400 }, boosts);
    }

    [Fact]
    public void MaterialSignal_FlagsMissedRecaptureAndLeftHangingQueen()
    {
        Board recapture = Fen.Parse("4k3/p7/3p4/4N3/8/8/8/4K3 b - - 0 1");
        Assert.True(ZapCrosspoint.GetMaterialSignal(in recapture, new Move(Square.A7, Square.A6)) >= 250);
        Assert.Equal(0, ZapCrosspoint.GetMaterialSignal(in recapture, new Move(Square.D6, Square.E5, MoveType.Capture)));

        Board queen = Fen.Parse("7k/8/8/q7/8/8/8/3R3K b - - 0 1");
        Assert.True(ZapCrosspoint.GetMaterialSignal(in queen, new Move(Square.A5, Square.D5)) >= 800);
        Assert.Equal(0, ZapCrosspoint.GetMaterialSignal(in queen, new Move(Square.A5, Square.A6)));
    }

    [Theory]
    [InlineData(Fen.StartPosition, "e2e4")]
    [InlineData("4k3/p7/3p4/4N3/8/8/8/4K3 b - - 0 1", "a7a6")]
    [InlineData("4k3/p7/3p4/4N3/8/8/8/4K3 b - - 0 1", "d6e5")]
    [InlineData("7k/8/8/q7/8/8/8/3R3K b - - 0 1", "a5d5")]
    [InlineData("q3r2k/8/8/8/8/8/8/R3K3 w - - 0 1", "e1d1")]
    [InlineData("7k/8/8/3pP3/8/8/8/K7 w - d6 0 1", "e5d6")]
    [InlineData("4r2k/8/8/8/8/8/q3R3/4K3 w - - 0 1", "e1d1")]
    [InlineData("1r5k/P7/8/8/8/8/8/7K w - - 0 1", "a7b8q")]
    public void MaterialSignal_CaptureOnlyPath_MatchesFullLegalMoveReference(string fen, string uci)
    {
        Board board = Fen.Parse(fen);
        MoveList legal = default;
        MoveGenerator.GenerateLegalMoves(in board, ref legal);
        Move move = Assert.Single(legal.AsSpan().ToArray(), m => m.ToUci() == uci);
        int missed = Math.Max(0, BestLegalCapture(in board) -
            Math.Max(0, move.IsCapture ? StaticExchangeEvaluator.Evaluate(in board, move) : 0));
        Board after = board;
        Assert.True(after.MakeMove(move, out BoardState state));
        int taken = StaticExchangeEvaluator.GetPieceValue(state.CapturedPiece.Type());
        int expected = Math.Max(missed, Math.Max(0, BestLegalCapture(in after) - taken));

        Assert.Equal(expected, ZapCrosspoint.GetMaterialSignal(in board, move));

        static int BestLegalCapture(in Board position)
        {
            MoveList moves = default;
            MoveGenerator.GenerateLegalMoves(in position, ref moves);
            int best = 0;
            foreach (Move capture in moves.AsSpan())
                if (capture.IsCapture) best = Math.Max(best, StaticExchangeEvaluator.Evaluate(in position, capture));
            return best;
        }
    }

    [Fact]
    public void WorkQueue_HangingQueen_IncreasesPickupPriorityWithoutEvalShock()
    {
        var queue = new ZapWorkQueue();
        Board quiet = Fen.Parse("7k/7p/8/8/8/8/P7/K7 w - - 0 1");
        Board hanging = Fen.Parse("7k/7p/8/8/3q4/8/P7/K2R4 w - - 0 1");
        queue.Enqueue(new ZapCrosspoint(quiet, 0, 3, 0, 0, CrosspointKind.Opportunity, 3, Move.Null));
        queue.Enqueue(new ZapCrosspoint(hanging, 1, 3, 0, 0, CrosspointKind.Opportunity, 3, Move.Null));

        Assert.True(queue.TryDequeue(out var picked));
        Assert.Equal(hanging.ZobristKey, picked.ZobristKey);
        Assert.True(queue.TryDequeue(out var remaining));
        Assert.Equal(quiet.ZobristKey, remaining.ZobristKey);
    }

    [Fact]
    public void WorkQueue_SharedPickup_PenalizesAllLinkedSpines()
    {
        var queue = new ZapWorkQueue();
        var shared = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);
        shared.Reset([new Move(Square.E2, Square.E4), new Move(Square.D2, Square.D4), new Move(Square.C2, Square.C4)]);
        uint generation = shared.NextGeneration();
        shared.UpdateCandidateBaseline(0, 0);
        shared.UpdateCandidateBaseline(1, 100);
        shared.UpdateCandidateBaseline(2, 100);

        queue.Enqueue(new ZapCrosspoint(board, linkedSpinesMask: 3UL, ply: 2, baselineScore: 0,
            parentEval: 0, shockMagnitude: 100, CrosspointKind.Opportunity, remainingHorizon: 2,
            Move.Null, priorityScore: 100, generation: generation));
        Board second = board;
        Assert.True(second.MakeMove(new Move(Square.E2, Square.E4, MoveType.DoublePawnPush), out _));
        queue.Enqueue(new ZapCrosspoint(second, 1, 2, 0, 0, CrosspointKind.Opportunity, 2, Move.Null, generation: generation));
        Board third = board;
        Assert.True(third.MakeMove(new Move(Square.D2, Square.D4, MoveType.DoublePawnPush), out _));
        queue.Enqueue(new ZapCrosspoint(third, 2, 2, 0, 0, CrosspointKind.Opportunity, 2, Move.Null, generation: generation));

        Assert.True(queue.TryDequeue(out var first, shared, generation));
        Assert.Equal(3UL, first.LinkedSpinesMask);
        Assert.True(queue.TryDequeue(out var next, shared, generation));
        Assert.Equal(2, next.RootCandidateIndex);
    }

    [Fact]
    public void WorkQueue_PrioritizesByShockMagnitudeDescending()
    {
        var queue = new ZapWorkQueue();
        Board board = Fen.Parse(Fen.StartPosition);

        queue.Enqueue(new ZapCrosspoint(board, 0, 1, 0, shockMagnitude: 50, CrosspointKind.Opportunity, 5, Move.Null));
        queue.Enqueue(new ZapCrosspoint(board, 0, 2, 0, shockMagnitude: 250, CrosspointKind.AdversarialRefutation, 5, Move.Null));
        queue.Enqueue(new ZapCrosspoint(board, 0, 3, 0, shockMagnitude: 150, CrosspointKind.Opportunity, 5, Move.Null));

        Assert.Equal(3, queue.Count);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(250, first.ShockMagnitude);

        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(150, second.ShockMagnitude);

        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal(50, third.ShockMagnitude);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void WorkQueue_DemotedSpine_DropsInPriority_ButIsNeverEvicted()
    {
        var queue = new ZapWorkQueue();
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());
        sharedState.UpdateCandidateBaseline(0, -500);
        sharedState.UpdateCandidateBaseline(1, 100);
        uint currentGen = sharedState.NextGeneration();

        Board other = board;
        Assert.True(other.MakeMove(new Move(Square.E2, Square.E4), out _));

        queue.Enqueue(new ZapCrosspoint(board, rootCandidateIndex: 0, ply: 1, baselineScore: 0, shockMagnitude: 100,
            CrosspointKind.Opportunity, remainingHorizon: 5, Move.Null, generation: currentGen));
        queue.Enqueue(new ZapCrosspoint(other, rootCandidateIndex: 1, ply: 1, baselineScore: 0, shockMagnitude: 100,
            CrosspointKind.Opportunity, remainingHorizon: 5, Move.Null, generation: currentGen));

        sharedState.SetRootMoveStatus(0, RootMoveStatus.Refuted);

        Assert.True(queue.TryDequeue(out var first, sharedState, currentGen));
        Assert.Equal(1, first.RootCandidateIndex);
        Assert.True(queue.TryDequeue(out var second, sharedState, currentGen));
        Assert.Equal(0, second.RootCandidateIndex);
    }

    [Fact]
    public void WorkQueue_EvictsStaleGeneration_InO1()
    {
        var queue = new ZapWorkQueue();
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);

        // Enqueue item from generation 1
        queue.Enqueue(new ZapCrosspoint(board, rootCandidateIndex: 0, ply: 1, baselineScore: 100, shockMagnitude: 100,
            CrosspointKind.AdversarialRefutation, remainingHorizon: 5, Move.Null, generation: 1));

        // Dequeue with currentGeneration 2 should discard stale gen 1 item
        Assert.False(queue.TryDequeue(out var item, sharedState, currentGeneration: 2));
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Responsibility_CreditsNegativeStaticEvalDrops_WithoutFollowupCapture()
    {
        // Both drops occur without an immediate follow-up capture exceeding AdaptiveShockBaseCp.
        ZapTrajectoryStep[] steps =
        [
            Step(Color.White, 50, -30),   // -80 cp (>= ShockThresholdCp=73)
            Step(Color.Black, -30, -25),  // +5 cp quiet reply (no shock trigger)
            Step(Color.White, -25, -75),  // -50 cp (>= QuietEvalDropMinCp=45, Move.Null is non-capture)
            Step(Color.Black, -75, -70),  // +5 cp quiet reply
        ];

        int[] blame = ZapWorkQueue.ComputeResponsibility(steps, Color.White, 2, new ZapParams());

        Assert.Equal(80, blame[0]);
        Assert.Equal(0, blame[1]);
        Assert.Equal(50, blame[2]);
        Assert.Equal(0, blame[3]);
    }

    [Fact]
    public void WorkQueue_QuietPawnPushAndCaptureToQuiet_OutrankWinningCapture()
    {
        var queue = new ZapWorkQueue();
        // Winning SEE>0 capture: White pawn captures undefended Black pawn on d5 (no larger capture exists).
        Board capBoard = Fen.Parse("4k3/8/8/3p4/4P3/8/8/4K3 w - - 0 1");
        Move winningCapture = new(Square.E4, Square.D5, MoveType.Capture);

        // Quiet safe pawn push a2a3 (zero captures, zero hanging pieces).
        Board quietBoard = Fen.Parse("4k3/8/8/8/8/8/P7/4K3 w - - 0 1");
        Move quietPawnPush = new(Square.A2, Square.A3);

        // Same quiet pawn push on a capture->quiet transition (prevWasCapture: true).
        Board capToQuietBoard = Fen.Parse("4k3/8/8/8/8/8/P6P/4K3 w - - 0 1");
        Move capToQuietPush = new(Square.H2, Square.H3);

        var parameters = new ZapParams();
        queue.Enqueue(new ZapCrosspoint(capBoard, 0, ply: 2, baselineScore: 0, shockMagnitude: 0,
            CrosspointKind.Opportunity, remainingHorizon: 4, winningCapture, parameters.CrosspointRootBiasWeight,
            role: ZapAgentRole.Promotion, parameters: parameters));
        queue.Enqueue(new ZapCrosspoint(quietBoard, 1, ply: 2, baselineScore: 0, shockMagnitude: 0,
            CrosspointKind.Opportunity, remainingHorizon: 4, quietPawnPush, parameters.CrosspointRootBiasWeight,
            role: ZapAgentRole.Promotion, prevWasCapture: false, parameters: parameters));
        queue.Enqueue(new ZapCrosspoint(capToQuietBoard, 2, ply: 2, baselineScore: 0, shockMagnitude: 0,
            CrosspointKind.Opportunity, remainingHorizon: 4, capToQuietPush, parameters.CrosspointRootBiasWeight,
            role: ZapAgentRole.Promotion, prevWasCapture: true, parameters: parameters));

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(capToQuietPush, first.OriginalMove);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(quietPawnPush, second.OriginalMove);
        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal(winningCapture, third.OriginalMove);
    }

    [Fact]
    public void WorkQueue_QuietAttackedDestination_AndDeepQuiet_ReflectTelemetryRisk()
    {
        var queue = new ZapWorkQueue();
        var parameters = new ZapParams();

        // White plays d2d3 kept defended by Ke2 against Black Rd8: Rd8xd3 has SEE < 0 so GetMaterialSignal == 0,
        // while after.IsSquareAttacked(d3, Black) is true, flagging destination-attacked quiet risk.
        Board attackedBoard = Fen.Parse("3rk3/8/8/8/8/8/1P1PK3/8 w - - 0 1");
        Move pawnIntoAttack = new(Square.D2, Square.D3);

        // Distinct ZobristKey via unthreatened h2 pawn so both crosspoints stay separate in the queue;
        // b2b3 lands safely outside Rd8/Ke8 attack range while d2 stays defended by Ke2.
        Board safeBoard = Fen.Parse("3rk3/8/8/8/8/8/1P1PK2P/8 w - - 0 1");
        Move pawnSafe = new(Square.B2, Square.B3);

        var cpSafe = new ZapCrosspoint(safeBoard, 0, ply: 2, baselineScore: 0, shockMagnitude: 0,
            CrosspointKind.Opportunity, remainingHorizon: 4, pawnSafe, parameters.CrosspointRootBiasWeight,
            role: ZapAgentRole.Promotion, parameters: parameters);
        var cpAttacked = new ZapCrosspoint(attackedBoard, 1, ply: 2, baselineScore: 0, shockMagnitude: 0,
            CrosspointKind.Opportunity, remainingHorizon: 4, pawnIntoAttack, parameters.CrosspointRootBiasWeight,
            role: ZapAgentRole.Promotion, parameters: parameters);

        Assert.Equal(cpSafe.TacticalPriority + parameters.CrosspointAttackedDestBonus, cpAttacked.TacticalPriority);

        // Verify deep quiet bonus applies at ply >= 6 while shallow root bias still keeps ply 2 ahead of ply 7.
        var cpDeepQuiet = new ZapCrosspoint(safeBoard, 2, ply: 7, baselineScore: 0, shockMagnitude: 0,
            CrosspointKind.Opportunity, remainingHorizon: 4, pawnSafe, parameters.CrosspointRootBiasWeight,
            role: ZapAgentRole.Promotion, parameters: parameters);
        Assert.Equal(cpSafe.TacticalPriority + parameters.CrosspointDeepQuietBonus, cpDeepQuiet.TacticalPriority);

        queue.Enqueue(in cpSafe);
        queue.Enqueue(in cpAttacked);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(pawnIntoAttack, first.OriginalMove);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(pawnSafe, second.OriginalMove);
    }

    [Fact]
    public void WorkQueue_CheckEvasionAndPromotion_DoNotOutrankQuietBlindspot()
    {
        var queue = new ZapWorkQueue();
        var parameters = new ZapParams();

        // Check evasion (no missed capture): telemetry shows 79.2% Top-3 and 0% 21+ -> no inflated +200 boost.
        Board inCheckBoard = Fen.Parse("4r1k1/8/8/8/8/8/8/4K3 w - - 0 1");
        Move kingStep = new(Square.E1, Square.D1);

        // Safe pawn push (worst rank category in telemetry: 13.21 mean rank, 44.4% >= 150 cp).
        Board quietBoard = Fen.Parse("4k3/8/8/8/8/8/P7/4K3 w - - 0 1");
        Move quietPawnPush = new(Square.A2, Square.A3);

        queue.Enqueue(new ZapCrosspoint(inCheckBoard, 0, ply: 3, baselineScore: 0, shockMagnitude: 0,
            CrosspointKind.Opportunity, remainingHorizon: 4, kingStep, parameters.CrosspointRootBiasWeight,
            role: ZapAgentRole.Promotion, parameters: parameters));
        queue.Enqueue(new ZapCrosspoint(quietBoard, 1, ply: 3, baselineScore: 0, shockMagnitude: 0,
            CrosspointKind.Opportunity, remainingHorizon: 4, quietPawnPush, parameters.CrosspointRootBiasWeight,
            role: ZapAgentRole.Promotion, parameters: parameters));

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(quietPawnPush, first.OriginalMove);
    }

    [Fact]
    public void WorkQueue_OffLineSubspine_RespectsHalfCapacitySoftCap()
    {
        var parameters = new ZapParams { MaxQueueEntries = 4 };
        var queue = new ZapWorkQueue(parameters);
        Board b0 = Fen.Parse(Fen.StartPosition);
        Board b1 = b0;
        Move m1 = new(Square.E2, Square.E4, MoveType.DoublePawnPush);
        Assert.True(b1.MakeMove(m1, out BoardState s1));

        // Fill queue to 50% capacity (2 entries out of MaxQueueEntries=4).
        queue.Enqueue(new ZapCrosspoint(b0, 0, ply: 1, baselineScore: 0, shockMagnitude: 10,
            CrosspointKind.Opportunity, remainingHorizon: 2, m1, parameters: parameters));
        queue.Enqueue(new ZapCrosspoint(b1, 0, ply: 2, baselineScore: 0, shockMagnitude: 10,
            CrosspointKind.Opportunity, remainingHorizon: 2, Move.Null, parameters: parameters));
        Assert.Equal(2, queue.TotalEntries);

        Move m2 = new(Square.E7, Square.E5, MoveType.DoublePawnPush);
        var edge = new ZapSpineTree.Edge
        {
            Steps =
            [
                new ZapTrajectoryStep(s1, b0.ZobristKey, m1, Color.White, 0, 0, 0),
                new ZapTrajectoryStep(default, b1.ZobristKey, m2, Color.Black, 0, 0, 0)
            ]
        };

        // Off-line subspine (spineGap > 0) is rejected past 50% capacity, while main-line (spineGap == 0) is accepted.
        int offLineOffered = queue.EnqueueSubspine(in b0, edge, 1UL, nodePly: 1, spineGap: 50, Color.White, generation: 1);
        Assert.Equal(0, offLineOffered);

        int mainLineOffered = queue.EnqueueSubspine(in b0, edge, 1UL, nodePly: 1, spineGap: 0, Color.White, generation: 1);
        Assert.Equal(1, mainLineOffered);
    }
}
