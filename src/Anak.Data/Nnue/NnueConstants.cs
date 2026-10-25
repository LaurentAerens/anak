namespace Anak.Data.Nnue;

/// <summary>
/// Architecture constants and binary serialization definitions for Anak NNUE networks.
/// </summary>
public static class NnueConstants
{
    /// <summary>
    /// Magic header identifier: "ANAK" (0x4B414E41).
    /// </summary>
    public const uint MagicHeader = 0x4B414E41;

    /// <summary>
    /// Supported network format version.
    /// </summary>
    public const uint FormatVersion = 1;

    /// <summary>
    /// Standard King-Bucketed NNUE architecture ID: 12,288 -> 512x2 -> 64 -> 1 with SCReLU.
    /// </summary>
    public const ushort ArchStandard = 0x0001;

    /// <summary>
    /// Micro NNUE architecture ID: 768 -> 16x2 -> 1.
    /// </summary>
    public const ushort ArchMicro = 0x0002;

    /// <summary>
    /// Number of king safety zone buckets with horizontal symmetry (ranks 0-1, 2-3, 4-5, 6-7 x files A-D).
    /// </summary>
    public const int KingBuckets = 16;

    /// <summary>
    /// Number of raw HalfKP piece-square input features (6 piece types * 2 colors * 64 squares).
    /// </summary>
    public const int RawFeatureCount = 768;

    /// <summary>
    /// Total number of bucketed HalfKP piece-square input features (16 buckets * 768 features = 12,288).
    /// </summary>
    public const int FeatureCount = KingBuckets * RawFeatureCount;

    /// <summary>
    /// Hidden dimension for Standard NNUE feature transformer (per perspective).
    /// </summary>
    public const int StandardHiddenDim = 512;

    /// <summary>
    /// Hidden layer 1 dimension for Standard NNUE (1024 -> 64).
    /// </summary>
    public const int StandardL1Dim = 64;

    /// <summary>
    /// Hidden dimension for Micro NNUE feature transformer (per perspective).
    /// </summary>
    public const int MicroHiddenDim = 16;

    /// <summary>
    /// Upper clamp limit for ClippedReLU activations (1.0 * WeightScale = 64).
    /// </summary>
    public const short ClippedReluMax = 64;

    /// <summary>
    /// Weight scaling divisor used during INT8 quantization.
    /// </summary>
    public const int WeightScale = 64;

    /// <summary>
    /// Output score scaling factor mapping network pawn output to centipawns (1.0 pawn = 100 cp).
    /// </summary>
    public const int OutputScale = 100;
}
