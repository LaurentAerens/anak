namespace Anak.Data.Nnue;

/// <summary>
/// Architecture constants and binary serialization definitions for Anak v1.2+ NNUE networks.
/// Architecture: (KingBuckets x 768 -> HL)x2 [INT16 QA=255, True Horizontal Mirroring]
///            -> OutputBuckets x (HL -> 1 Pairwise-Mul or 2*HL -> 1 SCReLU) [INT16 QB=64].
/// </summary>
public static class NnueConstants
{
    /// <summary>Magic header identifier: "ANAK" (0x4B414E41).</summary>
    public const uint MagicHeader = 0x4B414E41;

    /// <summary>Supported network format version.</summary>
    public const uint FormatVersion = 1;

    /// <summary>Perspective V2 Wide INT16 Single-Layer NNUE architecture ID.</summary>
    public const ushort ArchV2 = 0x0004;

    /// <summary>Number of king safety zone buckets with true horizontal symmetry (4 rank pairs x 4 files).</summary>
    public const int KingBuckets = 16;

    /// <summary>Number of raw HalfKP piece-square input features (6 piece types * 2 colors * 64 squares).</summary>
    public const int RawFeatureCount = 768;

    /// <summary>Total bucketed HalfKP input features (16 buckets * 768 = 12,288).</summary>
    public const int FeatureCount = KingBuckets * RawFeatureCount;

    /// <summary>Default hidden dimension per perspective.</summary>
    public const int V2DefaultHiddenDim = 1024;

    /// <summary>Default number of material-phase output buckets: (pieceCount - 2) / 4.</summary>
    public const int V2DefaultOutputBuckets = 8;

    /// <summary>Feature-transformer quantization scale (INT16 accumulator, clamp [0..255]).</summary>
    public const short V2QA = 255;

    /// <summary>Output-layer quantization scale (INT16 weights clamped to [-127..127]).</summary>
    public const int V2QB = 64;

    /// <summary>Activation flag for Squared Clipped ReLU (SCReLU).</summary>
    public const int V2ActScrelu = 0;

    /// <summary>Activation flag for Pairwise Clipped ReLU multiplication (a[i] * b[i]).</summary>
    public const int V2ActPairwise = 1;

    /// <summary>Centipawn output scale calibrated to Anak's 100 cp = 1 pawn search margins.</summary>
    public const int V2OutputScale = 200;
}
