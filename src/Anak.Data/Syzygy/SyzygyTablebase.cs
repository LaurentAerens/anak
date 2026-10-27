using Anak.Core;

namespace Anak.Data.Syzygy;

/// <summary>
/// Syzygy endgame tablebase manager (WDL .rtbw and DTZ .rtbz) backed by the managed <see cref="SyzygyProber"/>.
/// The maximum supported piece count is derived from the table files present in <see cref="SyzygyPath"/>:
/// the shipped 3-4 piece tables work out of the box, and dropping 5-6 piece tables into the directory enables them.
/// Probing fails gracefully (returns false) when files are missing so callers can fall back to search.
/// </summary>
public sealed class SyzygyTablebase : ITablebaseProber
{
    private readonly SyzygyProber _prober = new();
    private string _syzygyPath = string.Empty;
    private int _syzygyProbeDepth = 1;
    private ulong _tbHits;

    public string SyzygyPath
    {
        get => _syzygyPath;
        set
        {
            _syzygyPath = value ?? string.Empty;
            _prober.SetPath(ResolvePath(_syzygyPath));
        }
    }

    public int SyzygyProbeDepth
    {
        get => _syzygyProbeDepth;
        set => _syzygyProbeDepth = Math.Max(1, value);
    }

    /// <summary>
    /// Total number of successful tablebase probes (TbHits).
    /// </summary>
    public ulong TbHits => Interlocked.Read(ref _tbHits);

    /// <summary>
    /// Maximum piece count of loaded tablebases.
    /// </summary>
    public int MaxPieces => _prober.MaxPieces;

    public SyzygyTablebase()
    {
        string defaultPath = ResolvePath(string.Empty);
        if (!string.IsNullOrEmpty(defaultPath))
        {
            _prober.SetPath(defaultPath);
        }
    }

    public SyzygyTablebase(string path, int probeDepth = 1) : this()
    {
        SyzygyProbeDepth = probeDepth;
        SyzygyPath = path;
    }

    /// <summary>
    /// Resolves the effective Syzygy tablebase path across BaseDirectory and repository hierarchy.
    /// </summary>
    public static string ResolvePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            return Path.GetFullPath(path);

        if (!string.IsNullOrWhiteSpace(path))
            return path;

        string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string directCandidate = Path.Combine(baseDir, "syzygy");
        if (Directory.Exists(directCandidate))
            return Path.GetFullPath(directCandidate);

        string? parent = Directory.GetParent(baseDir)?.FullName;
        for (int i = 0; i < 7 && parent != null; i++)
        {
            string check = Path.Combine(parent, "syzygy");
            if (Directory.Exists(check))
                return Path.GetFullPath(check);

            parent = Directory.GetParent(parent)?.FullName;
        }

        return string.Empty;
    }

    /// <summary>
    /// Probes the Win-Draw-Loss value for the given position.
    /// Returns true on a successful tablebase hit and increments TbHits.
    /// Returns false if tablebase files are missing or the position cannot be probed.
    /// </summary>
    /// <param name="board">Board position to probe.</param>
    /// <param name="wdl">WDL value: 2 (win), 1 (cursed win), 0 (draw), -1 (blessed loss), -2 (loss).</param>
    public bool TryProbeWdl(in Board board, out int wdl)
    {
        if (_prober.TryProbeWdl(in board, out wdl))
        {
            Interlocked.Increment(ref _tbHits);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Probes the root position: returns the tablebase-best move and the DTZ of the position
    /// (positive = winning side to move, negative = losing, 0 = draw).
    /// </summary>
    /// <param name="board">Board position to probe.</param>
    /// <param name="bestMove">Optimal move selected by WDL / DTZ criteria.</param>
    /// <param name="dtz">Distance in plies to the nearest zeroing move (DTZ50'' value, signed).</param>
    public bool TryProbeDtz(in Board board, out Move bestMove, out int dtz)
    {
        if (_prober.TryProbeRoot(in board, out bestMove, out dtz))
        {
            Interlocked.Increment(ref _tbHits);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resets the TbHits counter to 0.
    /// </summary>
    public void ResetHits()
    {
        Interlocked.Exchange(ref _tbHits, 0UL);
    }
}
