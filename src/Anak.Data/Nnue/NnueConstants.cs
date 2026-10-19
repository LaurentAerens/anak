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
    /// Standard NNUE architecture ID: 768 -> 256x2 -> 32 -> 1.
    /// </summary>
    public const ushort ArchStandard = 0x0001;

    /// <summary>
    /// Micro NNUE architecture ID: 768 -> 16x2 -> 1.
    /// </summary>
    public const ushort ArchMicro = 0x0002;

    /// <summary>
    /// Number of HalfKP piece-square input features (6 piece types * 2 colors * 64 squares).
    /// </summary>
    public const int FeatureCount = 768;

    /// <summary>
    /// Hidden dimension for Standard NNUE feature transformer (per perspective).
    /// </summary>
    public const int StandardHiddenDim = 256;

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
