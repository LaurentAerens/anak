namespace Anak.Protocol.Datagen;

/// <summary>
/// Configuration parameters for the self-play datagen pipeline.
/// </summary>
public sealed class DatagenOptions
{
    public int Games { get; set; } = 100;
    public int Threads { get; set; } = Math.Min(16, Math.Max(1, Environment.ProcessorCount));
    public int Depth { get; set; } = 8;
    public int Nodes { get; set; } = 5000;
    public int RandomPlies { get; set; } = 8;
    public int MaxPliesPerGame { get; set; } = 300;
    public string OutputDir { get; set; } = Path.Combine("training", "data", "generated");
    public string? SyzygyPath { get; set; } = null;
    public bool ExportEpd { get; set; } = false;
    public int MicroSampleRate { get; set; } = 20; // 1 out of every 20 positions to micro.binpack (5%)
}
