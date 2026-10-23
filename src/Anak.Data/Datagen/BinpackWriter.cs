using System.Runtime.InteropServices;

namespace Anak.Data.Datagen;

/// <summary>
/// High-throughput buffered binary writer for Anak .binpack dataset files.
/// </summary>
public sealed class BinpackWriter : IDisposable
{
    public const int DefaultBufferSize = 8192;

    private readonly FileStream _stream;
    private readonly AnakBinpackRecord[] _buffer;
    private readonly string _tag;
    private int _bufferCount;
    private ulong _totalWritten;
    private bool _disposed;
    private readonly object _lock = new();

    public string Tag => _tag;
    public ulong TotalWritten => _totalWritten;

    public BinpackWriter(string filePath, string modelTag = "general", int bufferSize = DefaultBufferSize)
    {
        _tag = modelTag;
        _buffer = new AnakBinpackRecord[bufferSize];
        _bufferCount = 0;
        _totalWritten = 0;

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _stream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 65536);

        // Write initial header placeholder
        var header = AnakBinpackHeader.Create(modelTag, 0);
        WriteStruct(in header);
    }

    /// <summary>
    /// Appends a training record to the buffer, flushing to disk when full.
    /// </summary>
    public void Append(in AnakBinpackRecord record)
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BinpackWriter));

            _buffer[_bufferCount++] = record;
            if (_bufferCount >= _buffer.Length)
            {
                FlushInternal();
            }
        }
    }

    /// <summary>
    /// Appends a batch of records.
    /// </summary>
    public void AppendBatch(ReadOnlySpan<AnakBinpackRecord> records)
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BinpackWriter));

            for (int i = 0; i < records.Length; i++)
            {
                _buffer[_bufferCount++] = records[i];
                if (_bufferCount >= _buffer.Length)
                {
                    FlushInternal();
                }
            }
        }
    }

    /// <summary>
    /// Flushes all pending records to disk and updates the header record count.
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed) return;
            FlushInternal();
            UpdateHeaderCount();
        }
    }

    private void FlushInternal()
    {
        if (_bufferCount == 0) return;

        ReadOnlySpan<AnakBinpackRecord> slice = _buffer.AsSpan(0, _bufferCount);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(slice);
        _stream.Write(bytes);

        _totalWritten += (ulong)_bufferCount;
        _bufferCount = 0;
    }

    private void UpdateHeaderCount()
    {
        long currentPos = _stream.Position;
        _stream.Seek(16, SeekOrigin.Begin); // Offset 16 is RecordCount (uint64)
        Span<byte> countBytes = stackalloc byte[8];
        MemoryMarshal.Write(countBytes, in _totalWritten);
        _stream.Write(countBytes);
        _stream.Seek(currentPos, SeekOrigin.Begin);
        _stream.Flush();
    }

    private unsafe void WriteStruct<T>(in T val) where T : unmanaged
    {
        ReadOnlySpan<T> s = MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.CompilerServices.Unsafe.AsRef(in val), 1);
        ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(s);
        _stream.Write(b);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            FlushInternal();
            UpdateHeaderCount();
            _stream.Dispose();
        }
    }
}
