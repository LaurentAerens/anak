namespace Zanak.Time;

/// <summary>
/// Centralized, SPSA/Texel-tunable parameters for time allocation and dynamic depth budgeting.
/// All fields are mutable and can be tuned or overridden via UCI setoption.
/// </summary>
public sealed class TimeParams
{
    public static TimeParams Default { get; } = new();

    // Time division parameters
    public double SoftMoveTimeFactor { get; set; } = 0.95;
    public double MovesToGoDivisorOffset { get; set; } = 2.0;
    public double MovesToGoIncFactor { get; set; } = 0.80;
    public double SuddenDeathDivisor { get; set; } = 22.0;
    public double SuddenDeathIncFactor { get; set; } = 0.72;

    // Safety boundary clamps
    public double SoftClampMaxRatio { get; set; } = 0.40;
    public double HardClampMaxRatio { get; set; } = 0.80;
    public double HardLimitMultiplier { get; set; } = 2.5;
    public int HardBufferMs { get; set; } = 30;

    // Depth curve parameters
    public int HyperBulletBaseDepth { get; set; } = 3;
    public int HyperBulletMaxHorizon { get; set; } = 3;
    public int FastBulletBaseDepth { get; set; } = 5;
    public int FastBulletMaxHorizon { get; set; } = 5;
    public int BulletBaseDepth { get; set; } = 7;
    public int BulletMaxHorizon { get; set; } = 8;
    public int BlitzBaseDepth { get; set; } = 5;
    public int BlitzMaxHorizon { get; set; } = 12;
    public int RapidBaseDepth { get; set; } = 12;
    public int RapidMaxHorizon { get; set; } = 15;
    public int ClassicalBaseDepthOffset { get; set; } = 10;
    public double ClassicalLogScale { get; set; } = 3.0;
    public int ClassicalMaxBaseDepth { get; set; } = 18;
    public int ClassicalHorizonOffset { get; set; } = 8;

    public int AbsoluteMinBaseDepth { get; set; } = 1;
    public int AbsoluteMinMaxHorizon { get; set; } = 3;

    // Early termination & panic extensions
    public int StableDominanceMinDepth { get; set; } = 6;
    public int StableDominanceLeadCp { get; set; } = 150;
    public int StableDominanceIters { get; set; } = 3;
    public int PanicExtensionGapCp { get; set; } = 10;
    public double PanicExtensionFactor { get; set; } = 1.5;

    // Node check frequency
    public int CheckNodeInterval { get; set; } = 1024;
}
