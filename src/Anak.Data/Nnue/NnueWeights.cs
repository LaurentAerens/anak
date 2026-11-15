using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using File = System.IO.File;

namespace Anak.Data.Nnue;

/// <summary>
/// Contiguous 64-byte aligned memory container for quantized INT8/INT16 NNUE network weights.
/// Supports Standard (12,288 -> 512x2 -> 64 -> 1) and Shared-FT Multi-Expert MoE (12,288 -> 512x2 -> 6 x [64 -> 1]) architectures.
/// </summary>
public sealed unsafe class NnueWeights : IDisposable
{
    private void* _memoryBlock;
    private nuint _totalBytes;
    private bool _disposed;

    private sbyte*[] _expertHiddenWeights = [];
    private short*[] _expertHiddenBiases = [];
    private short*[] _expertOutputWeights = [];
    private int[] _expertOutputBiases = [];

    public ushort ArchId { get; private set; }
    public int HiddenDim { get; private set; }
    public int L1Dim { get; private set; }
    public int Buckets { get; private set; }
    public int Experts { get; private set; } = 1;

    // Pointers into 64-byte aligned memory block (for head 0 / Standard)
    public sbyte* FeatureWeights { get; private set; }   // [FeatureCount * HiddenDim]
    public short* FeatureBiases { get; private set; }    // [HiddenDim]
    public sbyte* HiddenWeights { get; private set; }    // [HiddenDim * 2 * L1Dim]
    public short* HiddenBiases { get; private set; }     // [L1Dim]
    public short* OutputWeights { get; private set; }    // [L1Dim]
    public int OutputBias { get; private set; }

    public bool IsStandard => ArchId == NnueConstants.ArchStandard;
    public bool IsMoE => ArchId == NnueConstants.ArchMoE;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte* GetHiddenWeights(int expert) => (uint)expert < (uint)Experts ? _expertHiddenWeights[expert] : HiddenWeights;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short* GetHiddenBiases(int expert) => (uint)expert < (uint)Experts ? _expertHiddenBiases[expert] : HiddenBiases;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short* GetOutputWeights(int expert) => (uint)expert < (uint)Experts ? _expertOutputWeights[expert] : OutputWeights;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOutputBias(int expert) => (uint)expert < (uint)Experts ? _expertOutputBiases[expert] : OutputBias;

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
            return LoadFromSpan(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static NnueWeights? LoadFromStream(Stream stream)
    {
        if (stream == null)
        {
            return null;
        }

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
            else
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return LoadFromSpan(ms.ToArray());
            }
        }
        catch
        {
            return null;
        }
    }

    public static NnueWeights? LoadFromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length < 16)
        {
            return null;
        }

        try
        {
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
            ushort headerPacked = BinaryPrimitives.ReadUInt16LittleEndian(span[14..16]);
            ReadOnlySpan<byte> payload = span[16..];

            int hiddenDim;
            int l1Dim;
            int buckets;
            int experts;

            if (archId == NnueConstants.ArchStandard)
            {
                hiddenDim = headerHiddenDim > 0 ? headerHiddenDim : NnueConstants.StandardHiddenDim;
                l1Dim = headerL1Dim > 0 ? headerL1Dim : NnueConstants.StandardL1Dim;
                buckets = headerPacked > 0 ? headerPacked : NnueConstants.KingBuckets;
                experts = 1;
            }
            else if (archId == NnueConstants.ArchMoE)
            {
                hiddenDim = headerHiddenDim > 0 ? headerHiddenDim : NnueConstants.StandardHiddenDim;
                l1Dim = headerL1Dim > 0 ? headerL1Dim : NnueConstants.StandardL1Dim;
                int lowByte = headerPacked & 0xFF;
                int highByte = (headerPacked >> 8) & 0xFF;
                buckets = lowByte > 0 ? lowByte : NnueConstants.KingBuckets;
                experts = highByte > 0 ? highByte : NnueConstants.ExpertCount;
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
                Buckets = buckets,
                Experts = experts
            };

            weights.AllocateAndLoad(payload, hiddenDim, l1Dim, buckets, experts);
            return weights;
        }
        catch
        {
            return null;
        }
    }

    private void AllocateAndLoad(ReadOnlySpan<byte> payload, int hiddenDim, int l1Dim, int buckets, int experts)
    {
        int featureCount = buckets * NnueConstants.RawFeatureCount;
        int ftWeightsBytes = featureCount * hiddenDim * sizeof(sbyte);
        int ftBiasesBytes = hiddenDim * sizeof(short);

        int hiddenWeightsBytes = hiddenDim * 2 * l1Dim * sizeof(sbyte);
        int hiddenBiasesBytes = l1Dim * sizeof(short);
        int outputWeightsBytes = l1Dim * sizeof(short);
        int outputBiasBytes = sizeof(int);

        int perExpertAlignedBytes = Align64(hiddenWeightsBytes) + Align64(hiddenBiasesBytes) + Align64(outputWeightsBytes);
        _totalBytes = (nuint)(Align64(ftWeightsBytes) + Align64(ftBiasesBytes) + (experts * perExpertAlignedBytes) + 256);
        _memoryBlock = NativeMemory.AlignedAlloc(_totalBytes, 64);
        NativeMemory.Clear(_memoryBlock, _totalBytes);

        _expertHiddenWeights = new sbyte*[experts];
        _expertHiddenBiases = new short*[experts];
        _expertOutputWeights = new short*[experts];
        _expertOutputBiases = new int[experts];

        byte* ptr = (byte*)_memoryBlock;
        int offset = 0;

        // 1. Shared Feature Weights
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

        // 2. Shared Feature Biases
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

        // 3. Per-Expert Heads (HiddenWeights, HiddenBiases, OutputWeights, OutputBias)
        for (int e = 0; e < experts; e++)
        {
            sbyte* hwPtr = (sbyte*)ptr;
            _expertHiddenWeights[e] = hwPtr;
            copyLen = Math.Min(hiddenWeightsBytes, Math.Max(0, payload.Length - offset));
            if (copyLen > 0)
            {
                fixed (byte* pSrc = payload.Slice(offset, copyLen))
                {
                    Buffer.MemoryCopy(pSrc, hwPtr, hiddenWeightsBytes, copyLen);
                }
                offset += copyLen;
            }
            ptr += Align64(hiddenWeightsBytes);

            short* hbPtr = (short*)ptr;
            _expertHiddenBiases[e] = hbPtr;
            copyLen = Math.Min(hiddenBiasesBytes, Math.Max(0, payload.Length - offset));
            if (copyLen > 0)
            {
                fixed (byte* pSrc = payload.Slice(offset, copyLen))
                {
                    Buffer.MemoryCopy(pSrc, hbPtr, hiddenBiasesBytes, copyLen);
                }
                offset += copyLen;
            }
            ptr += Align64(hiddenBiasesBytes);

            short* owPtr = (short*)ptr;
            _expertOutputWeights[e] = owPtr;
            copyLen = Math.Min(outputWeightsBytes, Math.Max(0, payload.Length - offset));
            if (copyLen > 0)
            {
                fixed (byte* pSrc = payload.Slice(offset, copyLen))
                {
                    Buffer.MemoryCopy(pSrc, owPtr, outputWeightsBytes, copyLen);
                }
                offset += copyLen;
            }
            ptr += Align64(outputWeightsBytes);

            if (payload.Length >= offset + outputBiasBytes)
            {
                _expertOutputBiases[e] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, outputBiasBytes));
                offset += outputBiasBytes;
            }
        }

        // Alias head 0 for Standard / single-head accessors
        HiddenWeights = _expertHiddenWeights[0];
        HiddenBiases = _expertHiddenBiases[0];
        OutputWeights = _expertOutputWeights[0];
        OutputBias = _expertOutputBiases[0];
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
