using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Cacheline-aligned (64-byte) bucket containing three 20-byte TT entries.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct TTCluster
{
    public TTEntry Entry0;
    public TTEntry Entry1;
    public TTEntry Entry2;
    private readonly uint _pad;
}

/// <summary>
/// High-performance lock-free Transposition Table with 64-byte cacheline alignment,
/// dynamic resizing, and depth/age-preferred replacement for Alpha-Beta search.
/// </summary>
public sealed unsafe class TranspositionTable : IDisposable
{
    private TTCluster* _clusters;
    private ulong _clusterCount;
    private ulong _mask;
    private byte _currentAge;

    public const int DefaultSizeMb = 64;

    public int SizeMb => (int)((_clusterCount * 64) / (1024 * 1024));
    public ulong ClusterCount => _clusterCount;
    public ulong EntryCount => _clusterCount * 3;
    public byte CurrentAge => _currentAge;

    /// <summary>
    /// Initializes the Transposition Table with the given size in Megabytes.
    /// </summary>
    /// <param name="sizeMb">Size in MB (rounded to nearest power-of-two clusters).</param>
    public TranspositionTable(int sizeMb = DefaultSizeMb)
    {
        Resize(sizeMb);
    }

    ~TranspositionTable()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_clusters != null)
        {
            NativeMemory.AlignedFree(_clusters);
            _clusters = null;
        }
        GC.SuppressFinalize(this);
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

        if (_clusters != null)
        {
            NativeMemory.AlignedFree(_clusters);
            _clusters = null;
        }

        _clusterCount = count;
        _mask = count - 1;
        nuint byteSize = (nuint)(count * 64);
        _clusters = (TTCluster*)NativeMemory.AlignedAlloc(byteSize, 64);
        NativeMemory.Clear(_clusters, byteSize);
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
        if (_clusters != null && _clusterCount > 0)
        {
            NativeMemory.Clear(_clusters, (nuint)(_clusterCount * 64));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref TTCluster GetCluster(ulong key)
    {
        ulong index = key & _mask;
        return ref _clusters[index];
    }

    /// <summary>
    /// Prefetches the 64-byte TT cluster for the given Zobrist key into CPU L1/L2 cache
    /// to eliminate memory latency before probing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Prefetch(ulong key)
    {
        if (System.Runtime.Intrinsics.X86.Sse.IsSupported)
        {
            System.Runtime.Intrinsics.X86.Sse.Prefetch0(_clusters + (key & _mask));
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

        if (cluster.Entry2.Key == key && cluster.Entry2.IsValid)
        {
            entry = cluster.Entry2;
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>
    /// Stores or updates an entry for Minimax / Alpha-Beta search.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong key, Move bestMove, byte depth, short score, byte age, TTFlags flags, byte expertId = 0, short staticEval = short.MinValue)
    {
        ref TTCluster cluster = ref GetCluster(key);

        // 1. If key matches any entry in the cluster, update in-place
        if (cluster.Entry0.Key == key)
        {
            UpdateMinimaxEntry(ref cluster.Entry0, key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        if (cluster.Entry1.Key == key)
        {
            UpdateMinimaxEntry(ref cluster.Entry1, key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        if (cluster.Entry2.Key == key)
        {
            UpdateMinimaxEntry(ref cluster.Entry2, key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        // 2. Replacement strategy: prefer uninitialized / invalid slot
        if (!cluster.Entry0.IsValid)
        {
            cluster.Entry0 = new TTEntry(key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        if (!cluster.Entry1.IsValid)
        {
            cluster.Entry1 = new TTEntry(key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        if (!cluster.Entry2.IsValid)
        {
            cluster.Entry2 = new TTEntry(key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        // 3. Score each slot by replacement priority (lower = better to replace):
        //    older age < different expert < shallower depth
        int s0 = ReplacementScore(in cluster.Entry0, age, expertId);
        int s1 = ReplacementScore(in cluster.Entry1, age, expertId);
        int s2 = ReplacementScore(in cluster.Entry2, age, expertId);

        if (s0 <= s1 && s0 <= s2)
        {
            cluster.Entry0 = new TTEntry(key, bestMove, depth, score, age, flags, expertId, staticEval);
        }
        else if (s1 <= s2)
        {
            cluster.Entry1 = new TTEntry(key, bestMove, depth, score, age, flags, expertId, staticEval);
        }
        else
        {
            cluster.Entry2 = new TTEntry(key, bestMove, depth, score, age, flags, expertId, staticEval);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReplacementScore(in TTEntry entry, byte age, byte expertId)
    {
        int ageBonus = entry.Age == age ? 512 : 0;
        int expertBonus = entry.ExpertId == expertId ? 256 : 0;
        return ageBonus + expertBonus + entry.Depth;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateMinimaxEntry(ref TTEntry existing, ulong key, Move bestMove, byte depth, short score, byte age, TTFlags flags, byte expertId, short staticEval)
    {
        if (flags == TTFlags.Exact || depth >= existing.Depth || existing.Age != age || existing.ExpertId != expertId)
        {
            existing.Key = key;
            existing.Depth = depth;
            existing.Score = score;
            existing.Flags = flags;
            existing.Age = age;
            existing.ExpertId = expertId;

            if (bestMove != Move.Null)
            {
                existing.BestMove = bestMove;
            }
        }
        else if (existing.BestMove == Move.Null && bestMove != Move.Null)
        {
            existing.BestMove = bestMove;
        }

        if (staticEval != short.MinValue)
        {
            existing.StaticEval = staticEval;
        }
    }

    /// <summary>
    /// Calculates the transposition table occupancy in permille (0 to 1000) by sampling.
    /// </summary>
    public int HashfullPermille()
    {
        if (_clusters == null || _clusterCount == 0) return 0;

        int sampleCount = (int)Math.Min(1000UL, _clusterCount);
        int usedEntries = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            ref readonly TTCluster cluster = ref _clusters[i];
            if (cluster.Entry0.IsValid && cluster.Entry0.Age == _currentAge) usedEntries++;
            if (cluster.Entry1.IsValid && cluster.Entry1.Age == _currentAge) usedEntries++;
            if (cluster.Entry2.IsValid && cluster.Entry2.Age == _currentAge) usedEntries++;
        }

        return (int)((usedEntries * 1000L) / (sampleCount * 3));
    }
}
