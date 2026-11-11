using System.Runtime.CompilerServices;

namespace Zanak.Search;

/// <summary>
/// Low-contention thread-safe priority queue ordering shock crosspoints by magnitude descending.
/// </summary>
public sealed class ZapWorkQueue
{
    private readonly PriorityQueue<ZapCrosspoint, int> _queue = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count == 0;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(in ZapCrosspoint crosspoint)
    {
        lock (_lock)
        {
            // Negate priority score so highest priority (high shock + root proximity) is polled first
            _queue.Enqueue(crosspoint, -crosspoint.PriorityScore);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out ZapCrosspoint crosspoint)
    {
        return TryDequeue(out crosspoint, null, 0);
    }

    /// <summary>
    /// Attempts to dequeue the highest-priority crosspoint.
    /// Incorporates O(1) Dynamic Queue Eviction: automatically drops crosspoints belonging to
    /// refuted/dead root moves or stale search generations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out ZapCrosspoint crosspoint, ZapSharedState? sharedState, uint currentGeneration)
    {
        lock (_lock)
        {
            while (_queue.Count > 0)
            {
                crosspoint = _queue.Dequeue();

                // Dynamic Queue Eviction:
                // If the crosspoint belongs to a stale generation or a refuted/dead root move,
                // discard it in O(1) and immediately inspect the next item!
                if (sharedState != null && currentGeneration != 0)
                {
                    if (crosspoint.Generation != currentGeneration ||
                        sharedState.IsRootMoveRefutedOrDead(crosspoint.RootCandidateIndex))
                    {
                        continue;
                    }
                }

                return true;
            }

            crosspoint = default;
            return false;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
        }
    }
}
