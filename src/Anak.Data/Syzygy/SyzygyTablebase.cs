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
            _wdl.SetPath(_syzygyPath);
            _dtz.SetPath(_syzygyPath);
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

    public SyzygyTablebase()
    {
        _wdl = new SyzygyWdl();
        _dtz = new SyzygyDtz();
    }

    public SyzygyTablebase(string path, int probeDepth = 1) : this()
    {
        SyzygyProbeDepth = probeDepth;
        SyzygyPath = path;
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
