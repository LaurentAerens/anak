using System.Buffers.Binary;
using System.Runtime.InteropServices;
using File = System.IO.File;

namespace Anak.Data.Nnue;

/// <summary>
/// Contiguous 64-byte aligned memory container for quantized INT8/INT16 NNUE network weights.
/// Supports both Standard (768 -> 512x2 -> 64 -> 1, plus legacy 768 -> 256x2 -> 32 -> 1) and Micro (768 -> 16x2 -> 1) architectures.
/// </summary>
public sealed unsafe class NnueWeights : IDisposable
{
    private void* _memoryBlock;
    private nuint _totalBytes;
    private bool _disposed;

    public ushort ArchId { get; private set; }
    public int HiddenDim { get; private set; }
    public int L1Dim { get; private set; }
    public int Buckets { get; private set; }

    // Pointers into 64-byte aligned memory block
    public sbyte* FeatureWeights { get; private set; }   // [FeatureCount * HiddenDim]
    public short* FeatureBiases { get; private set; }    // [HiddenDim]
    public sbyte* HiddenWeights { get; private set; }    // [HiddenDim * 2 * L1Dim] (Standard only)
    public short* HiddenBiases { get; private set; }     // [L1Dim] (Standard only)
    public short* OutputWeights { get; private set; }    // [L1Dim] (Standard) or [HiddenDim * 2] (Micro)
    public int OutputBias { get; private set; }

    public bool IsMicro => ArchId == NnueConstants.ArchMicro;
    public bool IsStandard => ArchId == NnueConstants.ArchStandard;

    ~NnueWeights()
    {
        Dispose(false);
    }

    public static NnueWeights? LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 16)
            {
                return null;
            }

            ReadOnlySpan<byte> span = bytes;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]);
            if (magic != NnueConstants.MagicHeader)
            {
                return null;
            }

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);
            if (version != NnueConstants.FormatVersion)
            {
                return null;
            }

            ushort archId = BinaryPrimitives.ReadUInt16LittleEndian(span[8..10]);
            ushort headerHiddenDim = BinaryPrimitives.ReadUInt16LittleEndian(span[10..12]);
            ushort headerL1Dim = BinaryPrimitives.ReadUInt16LittleEndian(span[12..14]);
            ushort headerBuckets = BinaryPrimitives.ReadUInt16LittleEndian(span[14..16]);
            ReadOnlySpan<byte> payload = span[16..];

            int hiddenDim;
            int l1Dim;
            int buckets;

            if (archId == NnueConstants.ArchStandard)
            {
                hiddenDim = headerHiddenDim > 0 ? headerHiddenDim : NnueConstants.StandardHiddenDim;
                l1Dim = headerL1Dim > 0 ? headerL1Dim : NnueConstants.StandardL1Dim;
                buckets = headerBuckets > 0 ? headerBuckets : NnueConstants.KingBuckets;
            }
            else if (archId == NnueConstants.ArchMicro)
            {
                hiddenDim = headerHiddenDim > 0 ? headerHiddenDim : NnueConstants.MicroHiddenDim;
                l1Dim = 0;
                if (headerBuckets > 0)
                {
                    buckets = headerBuckets;
                }
                else if (payload.Length >= NnueConstants.KingBuckets * NnueConstants.RawFeatureCount * hiddenDim)
                {
                    buckets = NnueConstants.KingBuckets;
                }
                else
                {
                    buckets = 1;
                }
            }
            else
            {
                return null;
            }

            var weights = new NnueWeights
            {
                ArchId = archId,
                HiddenDim = hiddenDim,
                L1Dim = l1Dim,
                Buckets = buckets
            };

            weights.AllocateAndLoad(payload, archId, hiddenDim, l1Dim, buckets);
            return weights;
        }
        catch
        {
            return null;
        }
    }

    private void AllocateAndLoad(ReadOnlySpan<byte> payload, ushort archId, int hiddenDim, int l1Dim, int buckets)
    {
        int featureCount = buckets * NnueConstants.RawFeatureCount;
        int ftWeightsBytes = featureCount * hiddenDim * sizeof(sbyte);
        int ftBiasesBytes = hiddenDim * sizeof(short);

        int hiddenWeightsBytes = archId == NnueConstants.ArchStandard ? (hiddenDim * 2 * l1Dim * sizeof(sbyte)) : 0;
        int hiddenBiasesBytes = archId == NnueConstants.ArchStandard ? (l1Dim * sizeof(short)) : 0;
        int outputWeightsBytes = (archId == NnueConstants.ArchStandard ? l1Dim : (hiddenDim * 2)) * sizeof(short);
        int outputBiasBytes = sizeof(int);

        _totalBytes = (nuint)(ftWeightsBytes + ftBiasesBytes + hiddenWeightsBytes + hiddenBiasesBytes + outputWeightsBytes + outputBiasBytes + 256);
        _memoryBlock = NativeMemory.AlignedAlloc(_totalBytes, 64);
        NativeMemory.Clear(_memoryBlock, _totalBytes);

        byte* ptr = (byte*)_memoryBlock;
        int offset = 0;

        // 1. Feature Weights
        FeatureWeights = (sbyte*)ptr;
        int copyLen = Math.Min(ftWeightsBytes, payload.Length - offset);
        if (copyLen > 0)
        {
            fixed (byte* pSrc = payload.Slice(offset, copyLen))
            {
                Buffer.MemoryCopy(pSrc, FeatureWeights, ftWeightsBytes, copyLen);
            }
            offset += copyLen;
        }
        ptr += Align64(ftWeightsBytes);

        // 2. Feature Biases
        FeatureBiases = (short*)ptr;
        copyLen = Math.Min(ftBiasesBytes, payload.Length - offset);
        if (copyLen > 0)
        {
            fixed (byte* pSrc = payload.Slice(offset, copyLen))
            {
                Buffer.MemoryCopy(pSrc, FeatureBiases, ftBiasesBytes, copyLen);
            }
            offset += copyLen;
        }
        ptr += Align64(ftBiasesBytes);

        // 3. Hidden layer (Standard only)
        if (archId == NnueConstants.ArchStandard)
        {
            HiddenWeights = (sbyte*)ptr;
            copyLen = Math.Min(hiddenWeightsBytes, payload.Length - offset);
            if (copyLen > 0)
            {
                fixed (byte* pSrc = payload.Slice(offset, copyLen))
                {
                    Buffer.MemoryCopy(pSrc, HiddenWeights, hiddenWeightsBytes, copyLen);
                }
                offset += copyLen;
            }
            ptr += Align64(hiddenWeightsBytes);

            HiddenBiases = (short*)ptr;
            copyLen = Math.Min(hiddenBiasesBytes, payload.Length - offset);
            if (copyLen > 0)
            {
                fixed (byte* pSrc = payload.Slice(offset, copyLen))
                {
                    Buffer.MemoryCopy(pSrc, HiddenBiases, hiddenBiasesBytes, copyLen);
                }
                offset += copyLen;
            }
            ptr += Align64(hiddenBiasesBytes);
        }

        // 4. Output Weights
        OutputWeights = (short*)ptr;
        copyLen = Math.Min(outputWeightsBytes, payload.Length - offset);
        if (copyLen > 0)
        {
            fixed (byte* pSrc = payload.Slice(offset, copyLen))
            {
                Buffer.MemoryCopy(pSrc, OutputWeights, outputWeightsBytes, copyLen);
            }
            offset += copyLen;
        }

        // 5. Output Bias
        if (payload.Length >= offset + sizeof(int))
        {
            OutputBias = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, sizeof(int)));
        }
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
