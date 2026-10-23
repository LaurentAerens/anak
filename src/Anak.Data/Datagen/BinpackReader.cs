using System.Runtime.InteropServices;

namespace Anak.Data.Datagen;

/// <summary>
/// High-performance stream reader for Anak .binpack dataset files.
/// </summary>
public sealed class BinpackReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly AnakBinpackHeader _header;
    private ulong _recordsRead;
    private bool _disposed;

    public AnakBinpackHeader Header => _header;
    public ulong TotalRecords => _header.RecordCount;
    public ulong RecordsRead => _recordsRead;

    public BinpackReader(string filePath)
    {
        _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        _recordsRead = 0;

        Span<byte> headerBytes = stackalloc byte[64];
        int read = _stream.Read(headerBytes);
        if (read < 64)
        {
            throw new InvalidDataException($"File '{filePath}' is too small to contain a 64-byte binpack header.");
        }

        _header = MemoryMarshal.Read<AnakBinpackHeader>(headerBytes);
        if (_header.GetMagicString() != "ANAKBINP")
        {
            throw new InvalidDataException($"Invalid binpack magic header: '{_header.GetMagicString()}'.");
        }
    }

    /// <summary>
    /// Reads the next record from the stream. Returns false at EOF.
    /// </summary>
    public bool ReadNext(out AnakBinpackRecord record)
    {
        if (_disposed || _recordsRead >= _header.RecordCount)
        {
            record = default;
            return false;
        }

        Span<byte> recordBytes = stackalloc byte[144];
        int read = _stream.Read(recordBytes);
        if (read < 144)
        {
            record = default;
            return false;
        }

        record = MemoryMarshal.Read<AnakBinpackRecord>(recordBytes);
        _recordsRead++;
        return true;
    }

    /// <summary>
    /// Reads a batch of records into the provided span. Returns the number of records read.
    /// </summary>
    public int ReadBatch(Span<AnakBinpackRecord> destination)
    {
        if (_disposed || _recordsRead >= _header.RecordCount)
        {
            return 0;
        }

        int toRead = (int)Math.Min((ulong)destination.Length, _header.RecordCount - _recordsRead);
        Span<byte> byteSpan = MemoryMarshal.AsBytes(destination[..toRead]);
        int bytesRead = _stream.Read(byteSpan);
        int recordsRead = bytesRead / 144;

        _recordsRead += (ulong)recordsRead;
        return recordsRead;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
