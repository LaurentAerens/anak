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
    public double SuddenDeathDivisor { get; set; } = 24.0;
    public double SuddenDeathIncFactor { get; set; } = 0.73;

    // Safety boundary clamps
    public double SoftClampMaxRatio { get; set; } = 0.40;
    public double HardClampMaxRatio { get; set; } = 0.80;
    public double HardLimitMultiplier { get; set; } = 2.5;
    public int HardBufferMs { get; set; } = 30;

    // Depth curve parameters (for zero-branching linear ZAP spines)
    public int HyperBulletBaseDepth { get; set; } = 8;
    public int HyperBulletMaxHorizon { get; set; } = 12;
    public int FastBulletBaseDepth { get; set; } = 10;
    public int FastBulletMaxHorizon { get; set; } = 14;
    public int BulletBaseDepth { get; set; } = 12;
    public int BulletMaxHorizon { get; set; } = 16;
    public int BlitzBaseDepth { get; set; } = 12;
    public int BlitzMaxHorizon { get; set; } = 18;
    public int RapidBaseDepth { get; set; } = 14;
    public int RapidMaxHorizon { get; set; } = 22;
    public int ClassicalBaseDepthOffset { get; set; } = 10;
    public double ClassicalLogScale { get; set; } = 3.0;
    public int ClassicalMaxBaseDepth { get; set; } = 18;
    public int ClassicalHorizonOffset { get; set; } = 8;

    public int AbsoluteMinBaseDepth { get; set; } = 1;
    public int AbsoluteMinMaxHorizon { get; set; } = 3;
    // With a clock budget, spines keep deepening past MaxDepthHorizon while time remains, up to this cap.
    public int ExtendedHorizonMaxPlies { get; set; } = 32;

    // Early termination & panic extensions
    public int StableDominanceMinDepth { get; set; } = 5;
    public int StableDominanceLeadCp { get; set; } = 149;
    public int StableDominanceIters { get; set; } = 4;
    public double StableDominanceMinSoftFraction { get; set; } = 0.34;
    public int PanicExtensionGapCp { get; set; } = 10;
    public double PanicExtensionFactor { get; set; } = 1.51;

    // Ply-1 survivor time scaling & dominant-leader verification
    public double SurvivorSoftScale2 { get; set; } = 0.35;
    public double SurvivorSoftScale3 { get; set; } = 0.50;
    public double SurvivorSoftScale4 { get; set; } = 0.73;
    public double SmallSurvivorMinSoftFraction { get; set; } = 0.15;
    public int DominantLeaderLeadCp { get; set; } = 153;
    public int SmallSurvivorVerifyCrosspoints { get; set; } = 6;
    public int LeaderVerifyCrosspoints { get; set; } = 13;
    public int ContenderQuickCheckPerRoot { get; set; } = 4;

    // Node check frequency
    public int CheckNodeInterval { get; set; } = 1024;
}
