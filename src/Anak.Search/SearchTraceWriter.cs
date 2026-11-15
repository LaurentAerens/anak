using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Anak.Core;

namespace Anak.Search;

/// <summary>
/// Synchronized JSONL position-graph diagnostics. Records actual search edges, including heuristic
/// null moves ("0000"), rather than generated moves or reconstructed principal variations.
/// Visit timestamps and the closing elapsed-time record share a monotonic search-local clock.
/// The search owner must join all writers before disposal and dispose before reporting bestmove.
/// </summary>
public sealed class SearchTraceWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private readonly long _started = Stopwatch.GetTimestamp();
    private ExceptionDispatchInfo? _failure;
    private bool _disposed;

    private SearchTraceWriter(string path, ulong root)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        try
        {
            _writer.WriteLine($"{{\"root\":\"{root:X16}\"}}");
        }
        catch
        {
            _writer.Dispose();
            throw;
        }
    }

    public static SearchTraceWriter? Open(string? path, ulong root) =>
        string.IsNullOrEmpty(path) || path == "<empty>" ? null : new SearchTraceWriter(path, root);

    public void WriteEdge(ulong from, ulong to, Move move)
    {
        lock (_sync)
        {
            if (_failure != null) return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                string seconds = Stopwatch.GetElapsedTime(_started).TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
                _writer.WriteLine($"{{\"from\":\"{from:X16}\",\"to\":\"{to:X16}\",\"move\":\"{(move.IsNull ? "0000" : move.ToUci())}\",\"seconds\":{seconds}}}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never throw I/O exceptions on a persistent search worker thread. Surface the first
                // failure synchronously on the search/UCI owner when it closes the trace instead.
                _failure = ExceptionDispatchInfo.Capture(ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_failure == null)
                {
                    string seconds = Stopwatch.GetElapsedTime(_started).TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
                    _writer.WriteLine($"{{\"elapsed_seconds\":{seconds}}}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failure ??= ExceptionDispatchInfo.Capture(ex);
            }
            try
            {
                _writer.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failure ??= ExceptionDispatchInfo.Capture(ex);
            }
            _failure?.Throw();
        }
    }
}
