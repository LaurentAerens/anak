using Anak.Core;
using Anak.Data.Datagen;

namespace Anak.Protocol.Datagen;

/// <summary>
/// Routes training positions into stratified NNUE expert binpacks:
/// - open.binpack
/// - closed.binpack
/// - endgame.binpack
/// - queenless.binpack
/// - imbalance.binpack
/// - attack.binpack
/// - volatility.binpack (high tactical swings / blunders >= 150 cp)
/// - micro.binpack (compact validation sample)
/// </summary>
public sealed class StratifiedBinpackWriter : IDisposable
{
    private readonly BinpackWriter _openWriter;
    private readonly BinpackWriter _closedWriter;
    private readonly BinpackWriter _endgameWriter;
    private readonly BinpackWriter _queenlessWriter;
    private readonly BinpackWriter _imbalanceWriter;
    private readonly BinpackWriter _attackWriter;
    private readonly BinpackWriter _volatilityWriter;
    private readonly BinpackWriter _microWriter;
    private readonly StreamWriter? _epdWriter;
    private readonly int _microSampleRate;
    private readonly ExpertRouting _routing;
    private ulong _totalPositions;
    private readonly object _lock = new();

    public ulong TotalPositions => _totalPositions;
    public ulong OpenCount => _openWriter.TotalWritten;
    public ulong ClosedCount => _closedWriter.TotalWritten;
    public ulong EndgameCount => _endgameWriter.TotalWritten;
    public ulong QueenlessCount => _queenlessWriter.TotalWritten;
    public ulong ImbalanceCount => _imbalanceWriter.TotalWritten;
    public ulong AttackCount => _attackWriter.TotalWritten;
    public ulong VolatilityCount => _volatilityWriter.TotalWritten;
    public ulong MicroCount => _microWriter.TotalWritten;

    public StratifiedBinpackWriter(string outputDir, int microSampleRate = 20, bool exportEpd = false, ExpertRouting? routing = null)
    {
        _microSampleRate = Math.Max(1, microSampleRate);
        _routing = routing ?? ExpertRouting.Default;
        Directory.CreateDirectory(outputDir);

        _openWriter = new BinpackWriter(Path.Combine(outputDir, "open.binpack"), "open");
        _closedWriter = new BinpackWriter(Path.Combine(outputDir, "closed.binpack"), "closed");
        _endgameWriter = new BinpackWriter(Path.Combine(outputDir, "endgame.binpack"), "endgame");
        _queenlessWriter = new BinpackWriter(Path.Combine(outputDir, "queenless.binpack"), "queenless");
        _imbalanceWriter = new BinpackWriter(Path.Combine(outputDir, "imbalance.binpack"), "imbalance");
        _attackWriter = new BinpackWriter(Path.Combine(outputDir, "attack.binpack"), "attack");
        _volatilityWriter = new BinpackWriter(Path.Combine(outputDir, "volatility.binpack"), "volatility");
        _microWriter = new BinpackWriter(Path.Combine(outputDir, "micro.binpack"), "micro");

        if (exportEpd)
        {
            _epdWriter = new StreamWriter(Path.Combine(outputDir, "training.epd"), append: false);
        }
    }

    /// <summary>
    /// Writes a single position record into the appropriate stratified binpack streams.
    /// </summary>
    public void WritePosition(in Board board, float targetEval, float targetWdl, bool isVolatile)
    {
        var record = AnakBinpackRecord.FromBoard(in board, targetEval, targetWdl, isVolatile);

        lock (_lock)
        {
            _totalPositions++;

            // 1. Tactical Volatility Stream
            if (isVolatile)
            {
                _volatilityWriter.Append(in record);
            }

            // 2. Stratified Routing by Topology using Tuned ExpertRouting
            NnueExpertType expert = PawnTopology.ClassifyExpert(in board, _routing);
            switch (expert)
            {
                case NnueExpertType.Endgame:
                    _endgameWriter.Append(in record);
                    break;
                case NnueExpertType.Queenless:
                    _queenlessWriter.Append(in record);
                    break;
                case NnueExpertType.Imbalance:
                    _imbalanceWriter.Append(in record);
                    break;
                case NnueExpertType.Attack:
                    _attackWriter.Append(in record);
                    break;
                case NnueExpertType.Open:
                    _openWriter.Append(in record);
                    break;
                case NnueExpertType.Closed:
                default:
                    _closedWriter.Append(in record);
                    break;
            }

            // 3. Uniform Subsampling for Micro Model
            if (_totalPositions % (ulong)_microSampleRate == 0)
            {
                _microWriter.Append(in record);
            }

            // 4. Optional EPD export
            if (_epdWriter != null)
            {
                string fen = Fen.ToFen(in board);
                string resultStr = targetWdl > 0.75f ? "1-0" : (targetWdl < 0.25f ? "0-1" : "1/2-1/2");
                _epdWriter.WriteLine($"{fen} c0 \"eval={targetEval:F0}; result={resultStr}\";");
            }
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            _openWriter.Flush();
            _closedWriter.Flush();
            _endgameWriter.Flush();
            _queenlessWriter.Flush();
            _imbalanceWriter.Flush();
            _attackWriter.Flush();
            _volatilityWriter.Flush();
            _microWriter.Flush();
            _epdWriter?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _openWriter.Dispose();
            _closedWriter.Dispose();
            _endgameWriter.Dispose();
            _queenlessWriter.Dispose();
            _imbalanceWriter.Dispose();
            _attackWriter.Dispose();
            _volatilityWriter.Dispose();
            _microWriter.Dispose();
            _epdWriter?.Dispose();
        }
    }
}
