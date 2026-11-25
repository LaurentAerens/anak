using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Packed 20-byte raw slot in a 64-byte TT cluster, using lockless XOR checksums
/// to prevent torn reads/writes under 100+ concurrent threads.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 20)]
public struct TTRawSlot
{
    public ulong KeyXor;
    public ulong DataLo;
    public uint DataHi;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryRead(ulong key, out TTEntry entry)
    {
        ulong keyXor = Volatile.Read(ref Unsafe.AsRef(in KeyXor));
        ulong dataLo = Volatile.Read(ref Unsafe.AsRef(in DataLo));
        uint dataHi = Volatile.Read(ref Unsafe.AsRef(in DataHi));

        if ((keyXor ^ dataLo ^ dataHi) == key)
        {
            byte depth = (byte)(dataLo >> 48);
            TTFlags flags = (TTFlags)(byte)dataHi;
            if (key != 0 && (depth > 0 || flags != TTFlags.None))
            {
                entry = TTEntry.Unpack(key, dataLo, dataHi);
                return true;
            }
        }

        entry = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryReadRaw(out ulong key, out ulong dataLo, out uint dataHi)
    {
        ulong keyXor = Volatile.Read(ref Unsafe.AsRef(in KeyXor));
        dataLo = Volatile.Read(ref Unsafe.AsRef(in DataLo));
        dataHi = Volatile.Read(ref Unsafe.AsRef(in DataHi));
        key = keyXor ^ dataLo ^ dataHi;
        byte depth = (byte)(dataLo >> 48);
        TTFlags flags = (TTFlags)(byte)dataHi;
        return key != 0 && (depth > 0 || flags != TTFlags.None);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ulong key, Move bestMove, byte depth, short score, byte age, TTFlags flags, byte expertId, short staticEval)
    {
        TTEntry.Pack(key, bestMove, depth, score, age, flags, expertId, staticEval, out ulong keyXor, out ulong dataLo, out uint dataHi);
        Volatile.Write(ref DataLo, dataLo);
        Volatile.Write(ref DataHi, dataHi);
        Volatile.Write(ref KeyXor, keyXor);
    }
}

/// <summary>
/// Cacheline-aligned (64-byte) bucket containing three 20-byte TT slots.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct TTCluster
{
    public TTRawSlot Slot0;
    public TTRawSlot Slot1;
    public TTRawSlot Slot2;
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
        ParallelClear(_clusters, count);
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
    /// Clears all entries in the Transposition Table in parallel across memory channels / NUMA nodes.
    /// </summary>
    public void Clear()
    {
        if (_clusters != null && _clusterCount > 0)
        {
            ParallelClear(_clusters, _clusterCount);
        }
    }

    private static void ParallelClear(TTCluster* clusters, ulong clusterCount)
    {
        // For small tables (<= 4 MB), single-threaded clear avoids ThreadPool overhead
        if (clusterCount <= 65536)
        {
            NativeMemory.Clear(clusters, (nuint)(clusterCount * 64));
            return;
        }

        int workers = Math.Clamp(Environment.ProcessorCount, 1, 256);
        ulong chunk = (clusterCount + (ulong)workers - 1) / (ulong)workers;
        Parallel.For(0, workers, i =>
        {
            ulong start = (ulong)i * chunk;
            if (start >= clusterCount) return;
            ulong len = Math.Min(chunk, clusterCount - start);
            NativeMemory.Clear(clusters + start, (nuint)(len * 64));
        });
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

        if (cluster.Slot0.TryRead(key, out entry)) return true;
        if (cluster.Slot1.TryRead(key, out entry)) return true;
        if (cluster.Slot2.TryRead(key, out entry)) return true;

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

        // Read all three slots once to match key, empty slot, or lowest replacement score
        bool v0 = cluster.Slot0.TryReadRaw(out ulong k0, out ulong lo0, out uint hi0);
        if (v0 && k0 == key)
        {
            UpdateSlot(ref cluster.Slot0, key, lo0, hi0, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        bool v1 = cluster.Slot1.TryReadRaw(out ulong k1, out ulong lo1, out uint hi1);
        if (v1 && k1 == key)
        {
            UpdateSlot(ref cluster.Slot1, key, lo1, hi1, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        bool v2 = cluster.Slot2.TryReadRaw(out ulong k2, out ulong lo2, out uint hi2);
        if (v2 && k2 == key)
        {
            UpdateSlot(ref cluster.Slot2, key, lo2, hi2, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        // Prefer uninitialized / invalid slot
        if (!v0)
        {
            cluster.Slot0.Write(key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        if (!v1)
        {
            cluster.Slot1.Write(key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        if (!v2)
        {
            cluster.Slot2.Write(key, bestMove, depth, score, age, flags, expertId, staticEval);
            return;
        }

        // Score each slot by replacement priority (lower = better to replace):
        // older age < different expert < shallower depth
        int s0 = ReplacementScore(lo0, hi0, age, expertId);
        int s1 = ReplacementScore(lo1, hi1, age, expertId);
        int s2 = ReplacementScore(lo2, hi2, age, expertId);

        if (s0 <= s1 && s0 <= s2)
        {
            cluster.Slot0.Write(key, bestMove, depth, score, age, flags, expertId, staticEval);
        }
        else if (s1 <= s2)
        {
            cluster.Slot1.Write(key, bestMove, depth, score, age, flags, expertId, staticEval);
        }
        else
        {
            cluster.Slot2.Write(key, bestMove, depth, score, age, flags, expertId, staticEval);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReplacementScore(ulong dataLo, uint dataHi, byte age, byte expertId)
    {
        byte entryAge = (byte)(dataLo >> 56);
        byte entryDepth = (byte)(dataLo >> 48);
        byte entryExpert = (byte)(dataHi >> 8);
        int ageBonus = entryAge == age ? 512 : 0;
        int expertBonus = entryExpert == expertId ? 256 : 0;
        return ageBonus + expertBonus + entryDepth;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateSlot(ref TTRawSlot slot, ulong key, ulong existingLo, uint existingHi, Move bestMove, byte depth, short score, byte age, TTFlags flags, byte expertId, short staticEval)
    {
        byte existingDepth = (byte)(existingLo >> 48);
        byte existingAge = (byte)(existingLo >> 56);
        byte existingExpert = (byte)(existingHi >> 8);
        Move existingMove = new Move((ushort)(existingLo >> 32));
        short existingStaticEval = (short)(ushort)(existingLo >> 16);

        byte newDepth = existingDepth;
        short newScore = (short)(ushort)existingLo;
        TTFlags newFlags = (TTFlags)(byte)existingHi;
        byte newAge = existingAge;
        byte newExpert = existingExpert;
        Move newMove = existingMove;
        short newStaticEval = existingStaticEval;

        if (flags == TTFlags.Exact || depth >= existingDepth || existingAge != age || existingExpert != expertId)
        {
            newDepth = depth;
            newScore = score;
            newFlags = flags;
            newAge = age;
            newExpert = expertId;

            if (bestMove != Move.Null)
            {
                newMove = bestMove;
            }
        }
        else if (existingMove == Move.Null && bestMove != Move.Null)
        {
            newMove = bestMove;
        }

        if (staticEval != short.MinValue)
        {
            newStaticEval = staticEval;
        }

        slot.Write(key, newMove, newDepth, newScore, newAge, newFlags, newExpert, newStaticEval);
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
            if (cluster.Slot0.TryReadRaw(out _, out ulong lo0, out _) && (byte)(lo0 >> 56) == _currentAge) usedEntries++;
            if (cluster.Slot1.TryReadRaw(out _, out ulong lo1, out _) && (byte)(lo1 >> 56) == _currentAge) usedEntries++;
            if (cluster.Slot2.TryReadRaw(out _, out ulong lo2, out _) && (byte)(lo2 >> 56) == _currentAge) usedEntries++;
        }

        return (int)((usedEntries * 1000L) / (sampleCount * 3));
    }
}
