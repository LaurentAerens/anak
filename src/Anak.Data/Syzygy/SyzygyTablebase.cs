using Anak.Core;

namespace Anak.Data.Syzygy;

/// <summary>
/// Syzygy 3-4-5-6 piece endgame tablebase manager handling WDL (.rtbw) and DTZ (.rtbz) probing,
/// hit statistics tracking, and graceful fallback when tablebase files are unavailable.
/// </summary>
public sealed class SyzygyTablebase
{
    private readonly SyzygyWdl _wdl;
    private readonly SyzygyDtz _dtz;
    private string _syzygyPath = string.Empty;
    private int _syzygyProbeDepth = 1;
    private ulong _tbHits;

    public string SyzygyPath
    {
        get => _syzygyPath;
        set
        {
            _syzygyPath = value ?? string.Empty;
            string effectivePath = ResolvePath(_syzygyPath);
            _wdl.SetPath(effectivePath);
            _dtz.SetPath(effectivePath);
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
    public int MaxPieces => Math.Max(_wdl.MaxPieces, _dtz.MaxPieces);

    public SyzygyTablebase()
    {
        _wdl = new SyzygyWdl();
        _dtz = new SyzygyDtz();
        string defaultPath = ResolvePath(string.Empty);
        if (!string.IsNullOrEmpty(defaultPath))
        {
            _wdl.SetPath(defaultPath);
            _dtz.SetPath(defaultPath);
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
    /// Returns false if tablebase files are missing or position cannot be probed.
    /// </summary>
    /// <param name="board">Board position to probe.</param>
    /// <param name="wdl">WDL value: 2 (win), 1 (cursed win), 0 (draw), -1 (blessed loss), -2 (loss).</param>
    public bool TryProbeWdl(in Board board, out int wdl)
    {
        if (_wdl.TryProbe(in board, out wdl))
        {
            Interlocked.Increment(ref _tbHits);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Probes the optimal DTZ move and distance-to-zeroing metric for the given position.
    /// Returns true on a successful tablebase hit and increments TbHits.
    /// Returns false if tablebase files are missing or position cannot be probed.
    /// </summary>
    /// <param name="board">Board position to probe.</param>
    /// <param name="bestMove">Optimal move selected by DTZ / WDL criteria.</param>
    /// <param name="dtz">Distance in plies to the nearest zeroing move.</param>
    public bool TryProbeDtz(in Board board, out Move bestMove, out int dtz)
    {
        if (_dtz.TryProbe(in board, _wdl, out bestMove, out dtz))
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
