using System.Runtime.CompilerServices;
using Anak.Core;

namespace Zanak.Search;

/// <summary>
/// Spine tree: every root spine and every subspine traced by an agent is an edge. A picked crosspoint
/// becomes a node whose children are the subspines traced from it (original move plus root-filter
/// survivors). An edge is worth its first expanded node, or its own greedy leaf score; a node is worth
/// the best of its children for the side to move. Values back up through the tree to the root spines,
/// one picked crosspoint at a time, never a full ply at a time. Nothing is ever pruned from the tree.
/// </summary>
internal sealed class ZapSpineTree
{
    internal sealed class Edge
    {
        public Node? Owner;
        public int RootIndex = -1;
        public ZapTrajectoryStep[] Steps = [];
        public int FirstPly;
        public int StartIdx;
        public int Leaf;
        public int Value;
        public bool Live = true;
    }

    internal sealed class Node
    {
        public ulong Key;
        public bool EngineToMove;
        public readonly List<Edge> Edges = new();
        public Edge? Best;
        public int Value;
        public bool Expanded => Edges.Count > 0;

        // Where the node's board comes from: a stored board (root spine crosspoints), or a step of the
        // subspine it was picked from (rebuilt by replaying moves from that subspine's own node).
        public StrongBox<Board>? BoardBox;
        public Edge? SourceEdge;
        public int SourceStep;
    }

    internal readonly record struct Child(ZapTrajectoryStep[] Steps, int Leaf);

    private readonly object _lock = new();
    private readonly Dictionary<ulong, Node> _nodes = new();
    private readonly Dictionary<ulong, List<Edge>> _edgesThrough = new();
    private Edge?[] _roots = [];

    /// <summary>Invoked under the tree lock for every root spine whose backed-up value or line changed.</summary>
    public Action<int, int, ZapTrajectoryStep[]>? RootChanged { get; set; }

    public int NodeCount { get { lock (_lock) return _nodes.Count; } }

    internal static ulong Key(ulong zobrist, int ply) => zobrist ^ ((ulong)ply << 56);

    public void Reset(int rootCount)
    {
        lock (_lock)
        {
            _nodes.Clear();
            _edgesThrough.Clear();
            _roots = new Edge?[rootCount];
        }
    }

    public bool HasRoot(int rootIndex)
    {
        lock (_lock) return (uint)rootIndex < (uint)_roots.Length && _roots[rootIndex] != null;
    }

    /// <summary>Installs (or replaces) a root spine and returns its backed-up value.</summary>
    public int SetRootSpine(int rootIndex, ReadOnlySpan<ZapTrajectoryStep> steps, int leaf)
    {
        lock (_lock)
        {
            if (rootIndex < 0) return leaf;
            if (rootIndex >= _roots.Length) Array.Resize(ref _roots, rootIndex + 1);
            if (_roots[rootIndex] is { } old) old.Live = false;
            var edge = new Edge { RootIndex = rootIndex, Steps = steps.ToArray(), FirstPly = 1, StartIdx = 0, Leaf = leaf };
            Register(edge);
            edge.Value = EdgeValue(edge);
            _roots[rootIndex] = edge;
            RootChanged?.Invoke(rootIndex, edge.Value, PrincipalLineLocked(rootIndex));
            return edge.Value;
        }
    }

    /// <summary>
    /// Expands the node at a picked crosspoint with its traced subspines and backs the new value up.
    /// Returns the child edges with their gap (distance from the node's value), in child order.
    /// </summary>
    public (Edge Edge, int Gap)[] Expand(in ZapCrosspoint cp, bool engineToMove, IReadOnlyList<Child> children, out int changedRoots)
    {
        lock (_lock)
        {
            ulong key = Key(cp.ZobristKey, cp.Ply);
            if (!_nodes.TryGetValue(key, out Node? node))
            {
                node = new Node { Key = key, EngineToMove = engineToMove };
                if (cp.BoardBox != null) node.BoardBox = cp.BoardBox;
                else
                {
                    node.SourceEdge = cp.TreeEdge;
                    node.SourceStep = cp.TreeStep;
                }
                _nodes[key] = node;
            }
            else if (node.BoardBox == null && cp.BoardBox != null)
            {
                node.BoardBox = cp.BoardBox;
            }
            var added = new List<Edge>(children.Count);
            foreach (Child child in children)
            {
                // A resumed pickup adds siblings; it must not discard previously explored branches.
                Move move = child.Steps.Length > 0 ? child.Steps[0].Move : Move.Null;
                int existing = node.Edges.FindIndex(e => e.Steps.Length > 0 && e.Steps[0].Move == move);
                if (existing >= 0)
                {
                    Edge old = node.Edges[existing];
                    if (old.Steps.Length > child.Steps.Length) continue;
                    old.Live = false;
                    node.Edges.RemoveAt(existing);
                }
                var edge = new Edge { Owner = node, Steps = child.Steps, FirstPly = cp.Ply, StartIdx = 1, Leaf = child.Leaf };
                Register(edge);
                edge.Value = EdgeValue(edge);
                node.Edges.Add(edge);
                added.Add(edge);
            }
            Recompute(node);

            var changed = new List<int>();
            BackUp(node, changed);
            if (RootChanged is { } publish)
            {
                foreach (int r in changed) publish(r, _roots[r]!.Value, PrincipalLineLocked(r));
            }
            changedRoots = changed.Count;

            var result = new (Edge, int)[added.Count];
            for (int i = 0; i < result.Length; i++) result[i] = (added[i], Math.Abs(added[i].Value - node.Value));
            return result;
        }
    }

    /// <summary>
    /// Rebuilds the board of a subspine crosspoint by replaying moves from the nearest node that kept
    /// a board. Returns false when the chain cannot be replayed.
    /// </summary>
    public static bool TryRebuildBoard(Edge edge, int step, out Board board)
    {
        board = default;
        var chain = new List<(Edge Edge, int Step)>(8) { (edge, step) };
        Node? node = edge.Owner;
        for (int guard = 0; node != null && node.BoardBox == null; guard++)
        {
            if (node.SourceEdge == null || guard > 4096) return false;
            chain.Add((node.SourceEdge, node.SourceStep));
            node = node.SourceEdge.Owner;
        }
        if (node?.BoardBox == null) return false;

        board = node.BoardBox.Value;
        for (int c = chain.Count - 1; c >= 0; c--)
        {
            (Edge e, int s) = chain[c];
            for (int j = 0; j < s; j++)
            {
                if (e.Steps[j].ZobristKey != board.ZobristKey || !board.MakeMove(e.Steps[j].Move, out _)) return false;
            }
        }
        return true;
    }

    public int RootValue(int rootIndex)
    {
        lock (_lock) return _roots[rootIndex]?.Value ?? 0;
    }

    /// <summary>The line the tree currently believes in for a root spine: follows the best subspine at each expanded node.</summary>
    public ZapTrajectoryStep[] PrincipalLine(int rootIndex)
    {
        lock (_lock) return PrincipalLineLocked(rootIndex);
    }

    private ZapTrajectoryStep[] PrincipalLineLocked(int rootIndex)
    {
        Edge? edge = (uint)rootIndex < (uint)_roots.Length ? _roots[rootIndex] : null;
        if (edge == null) return [];
        var line = new List<ZapTrajectoryStep>(64);
        for (int guard = 0; edge != null && guard < 512; guard++)
        {
            Node? next = FirstExpanded(edge, out int at);
            if (next == null || next.Best == null)
            {
                for (int j = 0; j < edge.Steps.Length; j++) line.Add(edge.Steps[j]);
                break;
            }
            for (int j = 0; j < at; j++) line.Add(edge.Steps[j]);
            edge = next.Best;
        }
        return line.ToArray();
    }

    private void Register(Edge edge)
    {
        for (int j = edge.StartIdx; j < edge.Steps.Length; j++)
        {
            ulong key = Key(edge.Steps[j].ZobristKey, edge.FirstPly + j);
            if (!_edgesThrough.TryGetValue(key, out List<Edge>? list))
            {
                list = new List<Edge>(2);
                _edgesThrough[key] = list;
            }
            else if (list.Count >= 8)
            {
                list.RemoveAll(e => !e.Live);
            }
            list.Add(edge);
        }
    }

    private Node? FirstExpanded(Edge edge, out int at)
    {
        for (int j = edge.StartIdx; j < edge.Steps.Length; j++)
        {
            if (_nodes.TryGetValue(Key(edge.Steps[j].ZobristKey, edge.FirstPly + j), out Node? node) && node.Expanded)
            {
                at = j;
                return node;
            }
        }
        at = -1;
        return null;
    }

    private int EdgeValue(Edge edge) => FirstExpanded(edge, out _) is { } node ? node.Value : edge.Leaf;

    private static bool Recompute(Node node)
    {
        Edge? best = null;
        foreach (Edge edge in node.Edges)
        {
            if (best == null || (node.EngineToMove ? edge.Value > best.Value : edge.Value < best.Value)) best = edge;
        }
        int value = best?.Value ?? node.Value;
        bool changed = value != node.Value || best != node.Best;
        node.Best = best;
        node.Value = value;
        return changed;
    }

    private void BackUp(Node start, List<int> changedRoots)
    {
        var pending = new Stack<Node>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            if (!_edgesThrough.TryGetValue(node.Key, out List<Edge>? edges)) continue;
            foreach (Edge edge in edges)
            {
                // Only edges whose first expanded node is this one see its value (or line) change.
                if (!edge.Live || FirstExpanded(edge, out _) != node) continue;
                edge.Value = node.Value;
                if (edge.Owner == null)
                {
                    if (!changedRoots.Contains(edge.RootIndex)) changedRoots.Add(edge.RootIndex);
                }
                else if (Recompute(edge.Owner))
                {
                    pending.Push(edge.Owner);
                }
            }
        }
    }
}
