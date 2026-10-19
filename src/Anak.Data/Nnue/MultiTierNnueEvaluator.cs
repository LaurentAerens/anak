using System.Numerics;
using Anak.Core;
using Anak.Data.Evaluation;
using Anak.Data.Syzygy;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Anak.Data.Nnue;

/// <summary>
/// Multi-Tier NNUE evaluator integrating root-pinned Open/Closed experts,
/// Micro-NNUE tactical watchdog evaluation, Syzygy tablebases, and seamless fallback to DefaultEvaluator.
/// </summary>
public sealed unsafe class MultiTierNnueEvaluator : IMultiTierEvaluator, IDisposable
{
    private NnueWeights? _openWeights;
    private NnueWeights? _closedWeights;
    private NnueWeights? _microWeights;
    private NnueWeights? _activeFullWeights;
    private readonly DefaultEvaluator _fallbackEvaluator;
    private bool _disposed;

    public bool UseNnue { get; set; } = true;
    public string NnueDirectory { get; set; } = "./networks";
    public bool BlunderWatchdog { get; set; } = true;
    public SyzygyTablebase? Tablebase { get; set; }

    public bool HasOpenModel => _openWeights != null;
    public bool HasClosedModel => _closedWeights != null;
    public bool HasMicroModel => _microWeights != null;
    public bool IsActive => UseNnue && (_openWeights != null || _closedWeights != null);

    public MultiTierNnueEvaluator(string? networkDir = null)
    {
        _fallbackEvaluator = new DefaultEvaluator();
        string dirToLoad = !string.IsNullOrWhiteSpace(networkDir) ? networkDir : NnueDirectory;
        LoadFromDirectory(dirToLoad);
    }

    /// <summary>
    /// Loads open.nnue, closed.nnue, and micro.nnue from the specified directory.
    /// Returns true if at least one model was successfully loaded.
    /// </summary>
    public bool LoadFromDirectory(string directoryPath)
    {
        string? resolvedDir = ResolveDirectory(directoryPath);
        if (resolvedDir == null)
        {
            return false;
        }

        NnueDirectory = resolvedDir;

        // Try load open network
        string openPath = Path.Combine(resolvedDir, "open.nnue");
        if (File.Exists(openPath))
        {
            _openWeights?.Dispose();
            _openWeights = NnueWeights.LoadFromFile(openPath);
        }

        // Try load closed network
        string closedPath = Path.Combine(resolvedDir, "closed.nnue");
        if (File.Exists(closedPath))
        {
            _closedWeights?.Dispose();
            _closedWeights = NnueWeights.LoadFromFile(closedPath);
        }

        // If a generic anak.nnue exists, load it as fallback for both open/closed
        string genericPath = Path.Combine(resolvedDir, "anak.nnue");
        if (_openWeights == null && _closedWeights == null && File.Exists(genericPath))
        {
            _openWeights = NnueWeights.LoadFromFile(genericPath);
            _closedWeights = _openWeights;
        }

        // Try load micro watchdog network
        string microPath = Path.Combine(resolvedDir, "micro.nnue");
        if (File.Exists(microPath))
        {
            _microWeights?.Dispose();
            _microWeights = NnueWeights.LoadFromFile(microPath);
        }

        // Default active model
        _activeFullWeights = _openWeights ?? _closedWeights;

        return _openWeights != null || _closedWeights != null || _microWeights != null;
    }

    private static string? ResolveDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            return Path.GetFullPath(directoryPath);
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.Combine(baseDir, directoryPath);
        if (Directory.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        candidate = Path.Combine(baseDir, "networks");
        if (Directory.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        string? parent = baseDir;
        for (int i = 0; i < 6 && parent != null; i++)
        {
            candidate = Path.Combine(parent, "networks");
            if (Directory.Exists(candidate) && (File.Exists(Path.Combine(candidate, "open.nnue")) || File.Exists(Path.Combine(candidate, "closed.nnue"))))
            {
                return Path.GetFullPath(candidate);
            }

            candidate = Path.Combine(parent, "training", "networks");
            if (Directory.Exists(candidate) && (File.Exists(Path.Combine(candidate, "open.nnue")) || File.Exists(Path.Combine(candidate, "closed.nnue"))))
            {
                return Path.GetFullPath(candidate);
            }

            parent = Directory.GetParent(parent)?.FullName;
        }

        return null;
    }

    /// <summary>
    /// Evaluates the root board's pawn topology once and pins the active full model
    /// (Open vs Closed) for the entire duration of the search, preventing evaluation jitter.
    /// </summary>
    public void SetRootTopology(in Board rootBoard)
    {
        if (_openWeights != null && _closedWeights != null)
        {
            bool isOpen = PawnTopology.IsOpen(in rootBoard);
            _activeFullWeights = isOpen ? _openWeights : _closedWeights;
        }
        else
        {
            _activeFullWeights = _openWeights ?? _closedWeights;
        }
    }

    /// <summary>
    /// Evaluates the board position using the active pinned full NNUE model.
    /// Falls back gracefully to DefaultEvaluator if NNUE is disabled or weights are absent.
    /// </summary>
    public Value Evaluate(in Board board, ReadOnlySpan<Move> legalMoves)
    {
        if (Tablebase != null && BitOperations.PopCount(board.Occupied) <= 6 && Tablebase.TryProbeWdl(in board, out int wdlScore))
        {
            if (wdlScore > 0) return Value.Win;
            if (wdlScore < 0) return Value.Loss;
            return Value.Draw;
        }

        if (!UseNnue)
        {
            return _fallbackEvaluator.Evaluate(in board, legalMoves);
        }

        if (_activeFullWeights == null)
        {
            if (_openWeights != null || _closedWeights != null)
            {
                SetRootTopology(in board);
            }

            if (_activeFullWeights == null)
            {
                return _fallbackEvaluator.Evaluate(in board, legalMoves);
            }
        }

        int dim = _activeFullWeights.HiddenDim;
        short* whiteAcc = stackalloc short[dim];
        short* blackAcc = stackalloc short[dim];

        NnueAccumulator.ComputeFull(in board, _activeFullWeights, whiteAcc, blackAcc);
        int cp = NnueInference.Evaluate(whiteAcc, blackAcc, _activeFullWeights, board.SideToMove);

        return new Value(cp);
    }

    /// <summary>
    /// Evaluates the board position using the ultra-fast Micro-NNUE model (1 SIMD register)
    /// for fast blunder and trap checking.
    /// </summary>
    public Value EvaluateMicro(in Board board)
    {
        if (Tablebase != null && BitOperations.PopCount(board.Occupied) <= 6 && Tablebase.TryProbeWdl(in board, out int wdlScore))
        {
            if (wdlScore > 0) return Value.Win;
            if (wdlScore < 0) return Value.Loss;
            return Value.Draw;
        }

        if (!UseNnue || _microWeights == null)
        {
            return Evaluate(in board, ReadOnlySpan<Move>.Empty);
        }

        const int dim = NnueConstants.MicroHiddenDim;
        short* whiteAcc = stackalloc short[dim];
        short* blackAcc = stackalloc short[dim];

        NnueAccumulator.ComputeFull(in board, _microWeights, whiteAcc, blackAcc);
        short* ourAcc = board.SideToMove == Color.White ? whiteAcc : blackAcc;
        short* theirAcc = board.SideToMove == Color.White ? blackAcc : whiteAcc;

        int cp = NnueInference.EvaluateMicro(ourAcc, theirAcc, _microWeights);
        return new Value(cp);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _openWeights?.Dispose();
            if (_closedWeights != _openWeights)
            {
                _closedWeights?.Dispose();
            }
            _microWeights?.Dispose();
            _disposed = true;
        }
    }
}
