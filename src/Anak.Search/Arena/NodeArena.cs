using System.Runtime.CompilerServices;

namespace Anak.Search;

/// <summary>
/// Pre-allocated flat contiguous arena for MCTS nodes to ensure zero GC allocations during search.
/// </summary>
public sealed class NodeArena
{
    private readonly MCTSNode[] _nodes;
    private int _usedCount;

    public const int DefaultCapacity = 2_000_000;

    /// <summary>
    /// Total maximum number of nodes this arena can hold.
    /// </summary>
    public int Capacity => _nodes.Length;

    /// <summary>
    /// Number of nodes currently allocated in the tree.
    /// </summary>
    public int UsedCount => _usedCount;

    /// <summary>
    /// Initializes the node arena with a pre-allocated contiguous array.
    /// </summary>
    /// <param name="capacity">Capacity in number of nodes (default: 2,000,000).</param>
    public NodeArena(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _nodes = new MCTSNode[capacity];
        _usedCount = 0;
    }

    /// <summary>
    /// Direct reference access to a node at specified index for zero-copy mutation.
    /// </summary>
    public ref MCTSNode this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _nodes[index];
    }

    /// <summary>
    /// Allocates a contiguous block of child nodes.
    /// </summary>
    /// <param name="count">Number of child nodes to allocate.</param>
    /// <param name="startIndex">The starting index in the arena.</param>
    /// <returns>True if allocation succeeded; false if arena is exhausted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllocateBlock(int count, out int startIndex)
    {
        if (count < 0 || _usedCount + count > _nodes.Length)
        {
            startIndex = -1;
            return false;
        }

        startIndex = _usedCount;
        _usedCount += count;

        for (int i = 0; i < count; i++)
        {
            _nodes[startIndex + i].Reset();
        }

        return true;
    }

    /// <summary>
    /// Checks whether the arena has remaining capacity to allocate the requested number of nodes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanAllocate(int count) => _usedCount + count <= _nodes.Length;

    /// <summary>
    /// Resets the arena pointer to zero, reclaiming all memory without garbage collection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        _usedCount = 0;
    }

    /// <summary>
    /// Returns a slice of child nodes starting at the given index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<MCTSNode> GetChildrenSpan(int startIndex, int count)
    {
        return _nodes.AsSpan(startIndex, count);
    }
}
