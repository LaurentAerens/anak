using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using File = System.IO.File;

namespace Anak.Data.Nnue;

/// <summary>
/// Contiguous 64-byte aligned memory container for quantized INT16 Anak v1.2+ NNUE weights (ArchV2 = 0x0004).
/// </summary>
public sealed unsafe class NnueWeights : IDisposable
{
    private void* _memoryBlock;
    private nuint _totalBytes;
    private bool _disposed;

    private short*[] _bucketOutputWeights = [];
    private int[] _bucketOutputBiases = [];

    public ushort ArchId { get; private set; }
    public int HiddenDim { get; private set; }
    public int Buckets { get; private set; }
    public int OutputBuckets { get; private set; } = 1;
    public int V2ActivationFlags { get; private set; }

    public short* FeatureWeights16 { get; private set; } // [Buckets * 768 * HiddenDim]
    public short* FeatureBiases { get; private set; }    // [HiddenDim]
    public short* OutputWeights { get; private set; }    // Bucket 0 alias
    public int OutputBias { get; private set; }          // Bucket 0 alias

    public bool IsV2 => ArchId == NnueConstants.ArchV2;
    public bool IsV2Pairwise => IsV2 && V2ActivationFlags == NnueConstants.V2ActPairwise;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short* GetV2OutputWeights(int outputBucket) =>
        (uint)outputBucket < (uint)OutputBuckets ? _bucketOutputWeights[outputBucket] : OutputWeights;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetV2OutputBias(int outputBucket) =>
        (uint)outputBucket < (uint)OutputBuckets ? _bucketOutputBiases[outputBucket] : OutputBias;

    ~NnueWeights() => Dispose(false);

    public static NnueWeights? LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        try
        {
            return LoadFromSpan(File.ReadAllBytes(filePath));
        }
        catch
        {
            return null;
        }
    }

    public static NnueWeights? LoadFromStream(Stream stream)
    {
        if (stream == null) return null;
        try
        {
            if (stream.CanSeek)
            {
                byte[] bytes = new byte[stream.Length];
                int totalRead = 0;
                while (totalRead < bytes.Length)
                {
                    int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                    if (read <= 0) break;
                    totalRead += read;
                }
                return LoadFromSpan(bytes.AsSpan(0, totalRead));
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return LoadFromSpan(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static NnueWeights? LoadFromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length < 16) return null;

        try
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);
            ushort archId = BinaryPrimitives.ReadUInt16LittleEndian(span[8..10]);
            if (magic != NnueConstants.MagicHeader || version != NnueConstants.FormatVersion || archId != NnueConstants.ArchV2)
            {
                return null;
            }

            ushort headerHiddenDim = BinaryPrimitives.ReadUInt16LittleEndian(span[10..12]);
            ushort headerOutputBuckets = BinaryPrimitives.ReadUInt16LittleEndian(span[12..14]);
            ushort headerPacked = BinaryPrimitives.ReadUInt16LittleEndian(span[14..16]);

            int hiddenDim = headerHiddenDim > 0 ? headerHiddenDim : NnueConstants.V2DefaultHiddenDim;
            int outputBuckets = headerOutputBuckets > 0 ? headerOutputBuckets : NnueConstants.V2DefaultOutputBuckets;
            int lowByte = headerPacked & 0xFF;
            int highByte = (headerPacked >> 8) & 0xFF;
            int buckets = lowByte > 0 ? lowByte : NnueConstants.KingBuckets;

            var weights = new NnueWeights
            {
                ArchId = archId,
                HiddenDim = hiddenDim,
                Buckets = buckets,
                OutputBuckets = outputBuckets,
                V2ActivationFlags = highByte
            };
            weights.AllocateAndLoad(span[16..], hiddenDim, buckets, outputBuckets);
            return weights;
        }
        catch
        {
            return null;
        }
    }

    private void AllocateAndLoad(ReadOnlySpan<byte> payload, int hiddenDim, int buckets, int outputBuckets)
    {
        int featureCount = buckets * NnueConstants.RawFeatureCount;
        int ftWeightsBytes = featureCount * hiddenDim * sizeof(short);
        int ftBiasesBytes = hiddenDim * sizeof(short);
        int outWeightsPerBucket = IsV2Pairwise ? hiddenDim : (hiddenDim * 2);
        int perBucketOutWeightsBytes = outWeightsPerBucket * sizeof(short);
        int outBiasBytes = sizeof(int);

        int perBucketAlignedBytes = Align64(perBucketOutWeightsBytes);
        _totalBytes = (nuint)(Align64(ftWeightsBytes) + Align64(ftBiasesBytes) + (outputBuckets * perBucketAlignedBytes) + 256);
        _memoryBlock = NativeMemory.AlignedAlloc(_totalBytes, 64);
        NativeMemory.Clear(_memoryBlock, _totalBytes);

        _bucketOutputWeights = new short*[outputBuckets];
        _bucketOutputBiases = new int[outputBuckets];

        byte* ptr = (byte*)_memoryBlock;
        int offset = 0;

        FeatureWeights16 = (short*)ptr;
        int copyLen = Math.Min(ftWeightsBytes, Math.Max(0, payload.Length - offset));
        if (copyLen > 0)
        {
            fixed (byte* pSrc = payload.Slice(offset, copyLen))
                Buffer.MemoryCopy(pSrc, FeatureWeights16, ftWeightsBytes, copyLen);
            offset += copyLen;
        }
        ptr += Align64(ftWeightsBytes);

        FeatureBiases = (short*)ptr;
        copyLen = Math.Min(ftBiasesBytes, Math.Max(0, payload.Length - offset));
        if (copyLen > 0)
        {
            fixed (byte* pSrc = payload.Slice(offset, copyLen))
                Buffer.MemoryCopy(pSrc, FeatureBiases, ftBiasesBytes, copyLen);
            offset += copyLen;
        }
        ptr += Align64(ftBiasesBytes);

        for (int b = 0; b < outputBuckets; b++)
        {
            short* owPtr = (short*)ptr;
            _bucketOutputWeights[b] = owPtr;
            copyLen = Math.Min(perBucketOutWeightsBytes, Math.Max(0, payload.Length - offset));
            if (copyLen > 0)
            {
                fixed (byte* pSrc = payload.Slice(offset, copyLen))
                    Buffer.MemoryCopy(pSrc, owPtr, perBucketOutWeightsBytes, copyLen);
                offset += copyLen;
            }
            ptr += Align64(perBucketOutWeightsBytes);

            if (payload.Length >= offset + outBiasBytes)
            {
                _bucketOutputBiases[b] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, outBiasBytes));
                offset += outBiasBytes;
            }
        }

        OutputWeights = _bucketOutputWeights[0];
        OutputBias = _bucketOutputBiases[0];
    }

    private static int Align64(int bytes) => (bytes + 63) & ~63;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (_memoryBlock != null)
            {
                NativeMemory.AlignedFree(_memoryBlock);
                _memoryBlock = null;
            }
            _disposed = true;
        }
    }
}
