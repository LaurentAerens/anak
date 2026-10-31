using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Cacheline-aligned (64-byte) bucket containing two 32-byte TT entries.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct TTCluster
{
    public TTEntry Entry0;
    public TTEntry Entry1;
}

/// <summary>
/// High-performance lock-free Transposition Table with 64-byte cacheline alignment,
/// dynamic resizing, DAG subtree reuse for MCTS, and depth-preferred replacement for Minimax.
/// </summary>
public sealed class TranspositionTable
{
    private TTCluster[] _clusters;
    private ulong _clusterCount;
    private ulong _mask;
    private byte _currentAge;

    public const int DefaultSizeMb = 64;

    public int SizeMb => (int)((_clusterCount * 64) / (1024 * 1024));
    public ulong ClusterCount => _clusterCount;
    public ulong EntryCount => _clusterCount * 2;
    public byte CurrentAge => _currentAge;

    /// <summary>
    /// Initializes the Transposition Table with the given size in Megabytes.
    /// </summary>
    /// <param name="sizeMb">Size in MB (rounded to nearest power-of-two clusters).</param>
    public TranspositionTable(int sizeMb = DefaultSizeMb)
    {
        _clusters = [];
        Resize(sizeMb);
    }

    /// <summary>
    /// Dynamically resizes the Transposition Table.
    /// </summary>
    /// <param name="sizeMb">Target size in Megabytes.</param>
    public void Resize(int sizeMb)
    {
        if (sizeMb <= 0) sizeMb = DefaultSizeMb;

        long targetBytes = (long)sizeMb * 1024 * 1024;
        long targetClusters = targetBytes / 64;

        if (targetClusters < 1) targetClusters = 1;

        // Round down to power of 2 for fast masking
        ulong count = 1;
        while ((count << 1) <= (ulong)targetClusters)
        {
            count <<= 1;
        }

        _clusterCount = count;
        _mask = count - 1;
        _clusters = new TTCluster[count];
        _currentAge = 1;
    }

    /// <summary>
    /// Advances the TT generation / age counter for a new search.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NewSearch()
    {
        _currentAge++;
        if (_currentAge == 0) _currentAge = 1;
    }

    /// <summary>
    /// Clears all entries in the Transposition Table.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_clusters);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref TTCluster GetCluster(ulong key)
    {
        ulong index = key & _mask;
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_clusters), (nint)index);
    }

    /// <summary>
    /// Prefetches the 64-byte TT cluster for the given Zobrist key into CPU L1/L2 cache
    /// to eliminate memory latency before probing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Prefetch(ulong key)
    {
        if (System.Runtime.Intrinsics.X86.Sse.IsSupported)
        {
            ulong index = key & _mask;
            ref TTCluster cluster = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_clusters), (nint)index);
            System.Runtime.Intrinsics.X86.Sse.Prefetch0(Unsafe.AsPointer(ref cluster));
        }
    }

    /// <summary>
    /// Probes the Transposition Table for an entry matching the given 64-bit Zobrist key.
    /// </summary>
    /// <param name="key">Zobrist key.</param>
    /// <param name="entry">Retrieved entry if found.</param>
    /// <returns>True if a valid entry was found; otherwise false.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(ulong key, out TTEntry entry)
    {
        ref TTCluster cluster = ref GetCluster(key);

        if (cluster.Entry0.Key == key && cluster.Entry0.IsValid)
        {
            entry = cluster.Entry0;
            return true;
        }

        if (cluster.Entry1.Key == key && cluster.Entry1.IsValid)
        {
            entry = cluster.Entry1;
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>
    /// Stores or updates an entry for Minimax / Alpha-Beta search.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong key, Move bestMove, byte depth, short score, byte age, TTFlags flags, byte expertId = 0)
    {
        ref TTCluster cluster = ref GetCluster(key);

        // 1. If key matches either entry in the cluster, update in-place
        if (cluster.Entry0.Key == key)
        {
            UpdateMinimaxEntry(ref cluster.Entry0, key, bestMove, depth, score, age, flags, expertId);
            return;
        }

        if (cluster.Entry1.Key == key)
        {
            UpdateMinimaxEntry(ref cluster.Entry1, key, bestMove, depth, score, age, flags, expertId);
            return;
        }

        // 2. Replacement strategy:
        // Prefer uninitialized / invalid slot
        if (!cluster.Entry0.IsValid)
        {
            cluster.Entry0 = new TTEntry(key, bestMove, depth, score, age, flags, expertId);
            return;
        }

        if (!cluster.Entry1.IsValid)
        {
            cluster.Entry1 = new TTEntry(key, bestMove, depth, score, age, flags, expertId);
            return;
        }

        // Prefer replacing slot with older age
        if (cluster.Entry0.Age != age && cluster.Entry1.Age == age)
        {
            cluster.Entry0 = new TTEntry(key, bestMove, depth, score, age, flags, expertId);
            return;
        }

        if (cluster.Entry1.Age != age && cluster.Entry0.Age == age)
        {
            cluster.Entry1 = new TTEntry(key, bestMove, depth, score, age, flags, expertId);
            return;
        }

        // If both slots are from older searches, prefer replacing slot from a different expert
        if (cluster.Entry0.Age != age && cluster.Entry1.Age != age)
        {
            if (cluster.Entry0.ExpertId != expertId && cluster.Entry1.ExpertId == expertId)
            {
                cluster.Entry0 = new TTEntry(key, bestMove, depth, score, age, flags, expertId);
                return;
            }

            if (cluster.Entry1.ExpertId != expertId && cluster.Entry0.ExpertId == expertId)
            {
                cluster.Entry1 = new TTEntry(key, bestMove, depth, score, age, flags, expertId);
                return;
            }
        }

        // Prefer replacing slot with shallower depth
        if (cluster.Entry0.Depth <= cluster.Entry1.Depth)
        {
            cluster.Entry0 = new TTEntry(key, bestMove, depth, score, age, flags, expertId);
        }
        else
        {
            cluster.Entry1 = new TTEntry(key, bestMove, depth, score, age, flags, expertId);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateMinimaxEntry(ref TTEntry existing, ulong key, Move bestMove, byte depth, short score, byte age, TTFlags flags, byte expertId)
    {
        // Overwrite score and depth if:
        // - New entry is Exact, OR
        // - New search depth is >= existing depth, OR
        // - Existing entry is from an older search generation, OR
        // - Existing entry is from a different expert
        if (flags == TTFlags.Exact || depth >= existing.Depth || existing.Age != age || existing.ExpertId != expertId)
        {
            existing.Key = key;
            existing.Depth = depth;
            existing.Score = score;
            existing.Flags = flags;
            existing.Age = age;
            existing.ExpertId = expertId;
            existing.Visits = Math.Max(1u, (uint)depth);
        }

        if (bestMove != Move.Null)
        {
            existing.BestMove = bestMove;
        }
    }

    /// <summary>
    /// Stores or merges an entry into the Transposition Table with DAG transposition reuse (MCTS).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong key, Move bestMove, uint visits, float value, float prior, byte age, TTFlags flags = TTFlags.Exact)
    {
        ref TTCluster cluster = ref GetCluster(key);

        // DAG Transposition Reuse: Check if either entry matches the key
        if (cluster.Entry0.Key == key)
        {
            UpdateMctsEntry(ref cluster.Entry0, key, bestMove, visits, value, prior, age, flags);
            return;
        }

        if (cluster.Entry1.Key == key)
        {
            UpdateMctsEntry(ref cluster.Entry1, key, bestMove, visits, value, prior, age, flags);
            return;
        }

        // Replacement strategy:
        // 1. Prefer uninitialized / empty slot
        if (!cluster.Entry0.IsValid)
        {
            cluster.Entry0 = new TTEntry(key, bestMove, visits, value, prior, age, flags);
            return;
        }

        if (!cluster.Entry1.IsValid)
        {
            cluster.Entry1 = new TTEntry(key, bestMove, visits, value, prior, age, flags);
            return;
        }

        // 2. Prefer replacing slot with older age
        if (cluster.Entry0.Age != age && cluster.Entry1.Age == age)
        {
            cluster.Entry0 = new TTEntry(key, bestMove, visits, value, prior, age, flags);
            return;
        }

        if (cluster.Entry1.Age != age && cluster.Entry0.Age == age)
        {
            cluster.Entry1 = new TTEntry(key, bestMove, visits, value, prior, age, flags);
            return;
        }

        // 3. Prefer replacing slot with fewer visits (least explored subtree)
        if (cluster.Entry0.Visits <= cluster.Entry1.Visits)
        {
            cluster.Entry0 = new TTEntry(key, bestMove, visits, value, prior, age, flags);
        }
        else
        {
            cluster.Entry1 = new TTEntry(key, bestMove, visits, value, prior, age, flags);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateMctsEntry(ref TTEntry existing, ulong key, Move bestMove, uint visits, float value, float prior, byte age, TTFlags flags)
    {
        // DAG merge: blend values weighted by visit counts and take maximum visits
        uint oldVisits = existing.Visits;
        float oldValue = existing.Value;

        if (oldVisits > 0 && visits > 0)
        {
            // Weighted average of value estimates across converging paths
            float total = oldVisits + visits;
            existing.Value = (oldValue * oldVisits + value * visits) / total;
            existing.Visits = Math.Max(oldVisits, visits);
        }
        else
        {
            existing.Value = value;
            existing.Visits = visits;
        }

        existing.Key = key;
        if (bestMove != Move.Null)
        {
            existing.BestMove = bestMove;
        }
        if (prior > 0.0f)
        {
            existing.Prior = prior;
        }
        existing.Age = age;
        existing.Flags = flags;
    }

    /// <summary>
    /// Calculates the transposition table occupancy in permille (0 to 1000) by sampling.
    /// </summary>
    public int HashfullPermille()
    {
        if (_clusters.Length == 0) return 0;

        int sampleCount = (int)Math.Min(1000UL, _clusterCount);
        int usedEntries = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            ref readonly TTCluster cluster = ref _clusters[i];
            if (cluster.Entry0.IsValid && cluster.Entry0.Age == _currentAge) usedEntries++;
            if (cluster.Entry1.IsValid && cluster.Entry1.Age == _currentAge) usedEntries++;
        }

        return (int)((usedEntries * 1000L) / (sampleCount * 2));
    }
}
