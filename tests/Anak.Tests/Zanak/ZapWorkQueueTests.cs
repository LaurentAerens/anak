using Anak.Core;
using Xunit;
using Zanak.Search;

namespace Anak.Tests.Zanak;

public sealed class ZapWorkQueueTests
{
    [Fact]
    public void WorkQueue_PrioritizesByShockMagnitudeDescending()
    {
        var queue = new ZapWorkQueue();
        Board board = Fen.Parse(Fen.StartPosition);

        queue.Enqueue(new ZapCrosspoint(board, 0, 1, 0, shockMagnitude: 50, CrosspointKind.Opportunity, 5, Move.Null));
        queue.Enqueue(new ZapCrosspoint(board, 0, 2, 0, shockMagnitude: 250, CrosspointKind.AdversarialRefutation, 5, Move.Null));
        queue.Enqueue(new ZapCrosspoint(board, 0, 3, 0, shockMagnitude: 120, CrosspointKind.Opportunity, 5, Move.Null));

        Assert.Equal(3, queue.Count);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(250, first.ShockMagnitude);

        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(120, second.ShockMagnitude);

        Assert.True(queue.TryDequeue(out var third));
        Assert.Equal(50, third.ShockMagnitude);

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void WorkQueue_EvictsRefutedRootMoves_InO1()
    {
        var queue = new ZapWorkQueue();
        var sharedState = new ZapSharedState();
        Board board = Fen.Parse(Fen.StartPosition);

        MoveList moves = default;
        MoveGenerator.GenerateLegalMoves(in board, ref moves);
        sharedState.Reset(moves.AsSpan().ToArray());
        uint currentGen = sharedState.NextGeneration();

        // Enqueue 3 crosspoints: root 0, root 1, root 2
        queue.Enqueue(new ZapCrosspoint(board, rootCandidateIndex: 0, ply: 1, baselineScore: 100, shockMagnitude: 100,
            CrosspointKind.AdversarialRefutation, remainingHorizon: 5, Move.Null, generation: currentGen));
        queue.Enqueue(new ZapCrosspoint(board, rootCandidateIndex: 1, ply: 1, baselineScore: 80, shockMagnitude: 90,
            CrosspointKind.Opportunity, remainingHorizon: 5, Move.Null, generation: currentGen));
        queue.Enqueue(new ZapCrosspoint(board, rootCandidateIndex: 2, ply: 1, baselineScore: 50, shockMagnitude: 80,
            CrosspointKind.Opportunity, remainingHorizon: 5, Move.Null, generation: currentGen));

        // Mark root move 0 as Refuted
        sharedState.SetRootMoveStatus(0, RootMoveStatus.Refuted);

        // Dequeue should skip root 0 in O(1) and return root 1
        Assert.True(queue.TryDequeue(out var dequeued, sharedState, currentGen));
        Assert.Equal(1, dequeued.RootCandidateIndex);

        // Mark root 2 as Dead
        sharedState.SetRootMoveStatus(2, RootMoveStatus.Dead);

        // Dequeue should skip root 2 and find nothing
        Assert.False(queue.TryDequeue(out var empty, sharedState, currentGen));
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
}
