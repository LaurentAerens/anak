using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Anak.Core;

namespace Zanak.Search;

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct ZapTTEntry
{
    public ulong Key;
    public Move BestMove;
    public short Score;
    public byte Depth;
    public byte Age;
    public byte Flags;

    public readonly bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Key != 0 && Depth > 0;
    }
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct ZapTTCluster
{
    public ZapTTEntry Entry0;
    public ZapTTEntry Entry1;
}

/// <summary>
/// Cacheline-aligned (64-byte) lock-free transposition table for Zanak.
/// </summary>
public sealed unsafe class ZapTranspositionTable : IDisposable
{
    private ZapTTCluster* _clusters;
    private ulong _clusterCount;
    private ulong _mask;
    private byte _currentAge;

    public const int DefaultSizeMb = 64;

    public int SizeMb => (int)((_clusterCount * 64) / (1024 * 1024));
    public ulong EntryCount => _clusterCount * 2;
    public byte CurrentAge => _currentAge;

    public ZapTranspositionTable(int sizeMb = DefaultSizeMb)
    {
        Resize(sizeMb);
    }

    ~ZapTranspositionTable()
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

    public void Resize(int sizeMb)
    {
        if (sizeMb <= 0) sizeMb = DefaultSizeMb;

        long targetBytes = (long)sizeMb * 1024 * 1024;
        long targetClusters = targetBytes / 64;

        if (!BitOperations.IsPow2((ulong)targetClusters))
        {
            targetClusters = (long)BitOperations.RoundUpToPowerOf2((ulong)targetClusters);
            if ((targetClusters * 64) > targetBytes && targetClusters > 1)
            {
                targetClusters >>= 1;
            }
        }

        if (targetClusters < 1024) targetClusters = 1024;

        if (_clusters != null)
        {
            NativeMemory.AlignedFree(_clusters);
            _clusters = null;
        }

        _clusterCount = (ulong)targetClusters;
        _mask = _clusterCount - 1;

        nuint byteSize = (nuint)(_clusterCount * 64);
        _clusters = (ZapTTCluster*)NativeMemory.AlignedAlloc(byteSize, 64);
        NativeMemory.Clear(_clusters, byteSize);
        _currentAge = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NewSearch()
    {
        _currentAge++;
        if (_currentAge == 0) _currentAge = 1;
    }

    public void Clear()
    {
        if (_clusters != null && _clusterCount > 0)
        {
            NativeMemory.Clear(_clusters, (nuint)(_clusterCount * 64));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref ZapTTCluster GetCluster(ulong key)
    {
        ulong index = key & _mask;
        return ref _clusters[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(ulong key, out ZapTTEntry entry)
    {
        ref ZapTTCluster cluster = ref GetCluster(key);

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong key, Move bestMove, byte depth, short score, byte flags = 1)
    {
        ref ZapTTCluster cluster = ref GetCluster(key);

        if (cluster.Entry0.Key == key)
        {
            cluster.Entry0.Key = key;
            if (!bestMove.IsNull) cluster.Entry0.BestMove = bestMove;
            cluster.Entry0.Depth = depth;
            cluster.Entry0.Score = score;
            cluster.Entry0.Age = _currentAge;
            cluster.Entry0.Flags = flags;
            return;
        }

        if (cluster.Entry1.Key == key)
        {
            cluster.Entry1.Key = key;
            if (!bestMove.IsNull) cluster.Entry1.BestMove = bestMove;
            cluster.Entry1.Depth = depth;
            cluster.Entry1.Score = score;
            cluster.Entry1.Age = _currentAge;
            cluster.Entry1.Flags = flags;
            return;
        }

        // Replacement: replace oldest or lowest depth
        if (cluster.Entry0.Age != _currentAge || cluster.Entry0.Depth <= depth)
        {
            cluster.Entry0.Key = key;
            cluster.Entry0.BestMove = bestMove;
            cluster.Entry0.Depth = depth;
            cluster.Entry0.Score = score;
            cluster.Entry0.Age = _currentAge;
            cluster.Entry0.Flags = flags;
        }
        else
        {
            cluster.Entry1.Key = key;
            cluster.Entry1.BestMove = bestMove;
            cluster.Entry1.Depth = depth;
            cluster.Entry1.Score = score;
            cluster.Entry1.Age = _currentAge;
            cluster.Entry1.Flags = flags;
        }
    }
}
