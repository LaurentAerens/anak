using System.Buffers.Binary;
using System.Runtime.InteropServices;
using File = System.IO.File;

namespace Anak.Data.Nnue;

/// <summary>
/// Contiguous 64-byte aligned memory container for quantized INT8/INT16 NNUE network weights.
/// Supports both Standard (768 -> 256x2 -> 32 -> 1) and Micro (768 -> 16x2 -> 1) architectures.
/// </summary>
public sealed unsafe class NnueWeights : IDisposable
{
    private void* _memoryBlock;
    private nuint _totalBytes;
    private bool _disposed;

    public ushort ArchId { get; private set; }
    public int HiddenDim { get; private set; }

    // Pointers into 64-byte aligned memory block
    public sbyte* FeatureWeights { get; private set; }   // [768 * HiddenDim]
    public short* FeatureBiases { get; private set; }    // [HiddenDim]
    public sbyte* HiddenWeights { get; private set; }    // [HiddenDim * 2 * 32] (Standard only)
    public short* HiddenBiases { get; private set; }     // [32] (Standard only)
    public short* OutputWeights { get; private set; }    // [32] (Standard) or [HiddenDim * 2] (Micro)
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
            int hiddenDim = archId switch
            {
                NnueConstants.ArchStandard => NnueConstants.StandardHiddenDim,
                NnueConstants.ArchMicro => NnueConstants.MicroHiddenDim,
                _ => 0
            };

            if (hiddenDim == 0)
            {
                return null;
            }

            var weights = new NnueWeights
            {
                ArchId = archId,
                HiddenDim = hiddenDim
            };

            weights.AllocateAndLoad(span[16..], archId, hiddenDim);
            return weights;
        }
        catch
        {
            return null;
        }
    }

    private void AllocateAndLoad(ReadOnlySpan<byte> payload, ushort archId, int hiddenDim)
    {
        int ftWeightsBytes = NnueConstants.FeatureCount * hiddenDim * sizeof(sbyte);
        int ftBiasesBytes = hiddenDim * sizeof(short);

        int hiddenWeightsBytes = archId == NnueConstants.ArchStandard ? (hiddenDim * 2 * 32 * sizeof(sbyte)) : 0;
        int hiddenBiasesBytes = archId == NnueConstants.ArchStandard ? (32 * sizeof(short)) : 0;
        int outputWeightsBytes = (archId == NnueConstants.ArchStandard ? 32 : (hiddenDim * 2)) * sizeof(short);
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
