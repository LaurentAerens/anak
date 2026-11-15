using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Zanak.Search;

internal readonly record struct ZapDiagnosticStart(string Root, bool Interleave, int BatchSize, int CostWeight);
internal readonly record struct ZapDiagnosticSummary(
    double RootFilterMs, long SeedingMs, long FirstBatchMs, int SeededRoots, string Diagnostics);

[JsonSerializable(typeof(ZapPickupTrace))]
[JsonSerializable(typeof(ZapBatchTrace))]
[JsonSerializable(typeof(ZapDiagnosticStart))]
[JsonSerializable(typeof(ZapDiagnosticSummary))]
internal partial class ZapDiagnosticJsonContext : JsonSerializerContext
{
}

internal sealed class ZapDiagnosticsWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public ZapDiagnosticsWriter(string path)
    {
        _writer = new StreamWriter(path, append: false);
    }

    public void Start(ulong root, ZapParams parameters) => Write("start",
        new ZapDiagnosticStart($"{root:X16}", parameters.InterleaveRootSeeding,
            parameters.MaxSiblingExploration, parameters.CrosspointCostWeight),
        ZapDiagnosticJsonContext.Default.ZapDiagnosticStart);
    public void Pickup(ZapPickupTrace pickup) => Write("pickup", pickup, ZapDiagnosticJsonContext.Default.ZapPickupTrace);
    public void Batch(ZapBatchTrace batch) => Write("batch", batch, ZapDiagnosticJsonContext.Default.ZapBatchTrace);
    public void Summary(double rootFilterMs, long seedingMs, long firstBatchMs, int seededRoots, string diagnostics) =>
        Write("summary", new ZapDiagnosticSummary(rootFilterMs, seedingMs, firstBatchMs, seededRoots, diagnostics),
            ZapDiagnosticJsonContext.Default.ZapDiagnosticSummary);

    private void Write<T>(string type, T value, JsonTypeInfo<T> typeInfo)
    {
        lock (_lock)
        {
            _writer.Write("{\"type\":\"");
            _writer.Write(type);
            _writer.Write("\",\"data\":");
            _writer.Write(JsonSerializer.Serialize(value, typeInfo));
            _writer.WriteLine("}");
        }
    }

    public void Dispose() => _writer.Dispose();
}
