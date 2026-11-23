using Anak.Core;
using Anak.Data.Nnue;
using Xunit;

namespace Anak.Tests.Data;

public sealed class NnueInferenceTests
{
    [Fact]
    public void NnueConstants_ArchV2Dimensions()
    {
        Assert.Equal(16, NnueConstants.KingBuckets);
        Assert.Equal(768, NnueConstants.RawFeatureCount);
        Assert.Equal(12_288, NnueConstants.FeatureCount);
        Assert.Equal(1024, NnueConstants.V2DefaultHiddenDim);
        Assert.Equal(8, NnueConstants.V2DefaultOutputBuckets);
        Assert.Equal(255, NnueConstants.V2QA);
        Assert.Equal(64, NnueConstants.V2QB);
    }

    [Fact]
    public void MultiTierEvaluator_LoadsEmbeddedOrDiskV2Network_AndEvaluatesReasonably()
    {
        using var evaluator = new MultiTierNnueEvaluator();
        Assert.True(evaluator.IsActive);
        Assert.NotNull(evaluator.ActiveWeights);
        Assert.True(evaluator.ActiveWeights!.IsV2);

        Board start = Fen.Parse(Fen.StartPosition);
        Board plusQ = Fen.Parse("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Board minusQ = Fen.Parse("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNB1KBNR w KQkq - 0 1");

        int evalStart = evaluator.Evaluate(in start).Raw;
        int evalPlusQ = evaluator.Evaluate(in plusQ).Raw;
        int evalMinusQ = evaluator.Evaluate(in minusQ).Raw;

        Assert.InRange(evalStart, -100, 100);
        Assert.True(evalPlusQ > 500, $"Expected +Queen > 500cp, got {evalPlusQ}");
        Assert.True(evalMinusQ < -500, $"Expected -Queen < -500cp, got {evalMinusQ}");
    }
}
