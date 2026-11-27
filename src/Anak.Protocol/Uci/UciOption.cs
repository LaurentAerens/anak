using Anak.Search;

namespace Anak.Protocol.Uci;

public enum UciOptionType
{
    Check,
    Spin,
    Combo,
    Button,
    String
}

public abstract class UciOption
{
    public string Name { get; }
    public UciOptionType Type { get; }

    protected UciOption(string name, UciOptionType type)
    {
        Name = name;
        Type = type;
    }

    public abstract string GetUciDeclaration();
    public abstract bool TrySetValue(string? value);
    public abstract string GetCurrentValueString();
}

public sealed class SpinOption : UciOption
{
    private int _value;

    public int MinValue { get; }
    public int MaxValue { get; }
    public int DefaultValue { get; }

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, MinValue, MaxValue);
            if (_value != clamped)
            {
                _value = clamped;
                ValueChanged?.Invoke(_value);
            }
        }
    }

    public event Action<int>? ValueChanged;

    public SpinOption(string name, int defaultValue, int min, int max, Action<int>? onChange = null)
        : base(name, UciOptionType.Spin)
    {
        MinValue = min;
        MaxValue = max;
        DefaultValue = Math.Clamp(defaultValue, min, max);
        _value = DefaultValue;
        if (onChange != null)
        {
            ValueChanged += onChange;
        }
    }

    public override string GetUciDeclaration() =>
        $"option name {Name} type spin default {DefaultValue} min {MinValue} max {MaxValue}";

    public override bool TrySetValue(string? value)
    {
        if (int.TryParse(value, out var parsed))
        {
            Value = parsed;
            return true;
        }
        return false;
    }

    public override string GetCurrentValueString() => _value.ToString();
}

public sealed class CheckOption : UciOption
{
    private bool _value;

    public bool DefaultValue { get; }

    public bool Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                ValueChanged?.Invoke(_value);
            }
        }
    }

    public event Action<bool>? ValueChanged;

    public CheckOption(string name, bool defaultValue, Action<bool>? onChange = null)
        : base(name, UciOptionType.Check)
    {
        DefaultValue = defaultValue;
        _value = defaultValue;
        if (onChange != null)
        {
            ValueChanged += onChange;
        }
    }

    public override string GetUciDeclaration() =>
        $"option name {Name} type check default {(DefaultValue ? "true" : "false")}";

    public override bool TrySetValue(string? value)
    {
        if (bool.TryParse(value, out var parsed))
        {
            Value = parsed;
            return true;
        }
        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase))
        {
            Value = true;
            return true;
        }
        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase))
        {
            Value = false;
            return true;
        }
        return false;
    }

    public override string GetCurrentValueString() => _value ? "true" : "false";
}

public sealed class StringOption : UciOption
{
    private string _value;

    public string DefaultValue { get; }

    public string Value
    {
        get => _value;
        set
        {
            var newVal = value ?? string.Empty;
            if (_value != newVal)
            {
                _value = newVal;
                ValueChanged?.Invoke(_value);
            }
        }
    }

    public event Action<string>? ValueChanged;

    public StringOption(string name, string defaultValue, Action<string>? onChange = null)
        : base(name, UciOptionType.String)
    {
        DefaultValue = defaultValue ?? string.Empty;
        _value = DefaultValue;
        if (onChange != null)
        {
            ValueChanged += onChange;
        }
    }

    public override string GetUciDeclaration()
    {
        var defaultStr = string.IsNullOrEmpty(DefaultValue) ? "<empty>" : DefaultValue;
        return $"option name {Name} type string default {defaultStr}";
    }

    public override bool TrySetValue(string? value)
    {
        Value = (value == "<empty>" || value == null) ? string.Empty : value;
        return true;
    }

    public override string GetCurrentValueString() => _value;
}

public sealed class ButtonOption : UciOption
{
    private readonly Action _onClick;

    public ButtonOption(string name, Action onClick)
        : base(name, UciOptionType.Button)
    {
        _onClick = onClick ?? throw new ArgumentNullException(nameof(onClick));
    }

    public override string GetUciDeclaration() =>
        $"option name {Name} type button";

    public override bool TrySetValue(string? value)
    {
        _onClick();
        return true;
    }

    public override string GetCurrentValueString() => string.Empty;
}

public sealed class ComboOption : UciOption
{
    private string _value;

    public string DefaultValue { get; }
    public IReadOnlyList<string> VarOptions { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (VarOptions.Contains(value, StringComparer.OrdinalIgnoreCase) && _value != value)
            {
                _value = value;
                ValueChanged?.Invoke(_value);
            }
        }
    }

    public event Action<string>? ValueChanged;

    public ComboOption(string name, string defaultValue, string[] varOptions, Action<string>? onChange = null)
        : base(name, UciOptionType.Combo)
    {
        VarOptions = varOptions;
        DefaultValue = defaultValue;
        _value = defaultValue;
        if (onChange != null)
        {
            ValueChanged += onChange;
        }
    }

    public override string GetUciDeclaration()
    {
        var vars = string.Join(" ", VarOptions.Select(v => $"var {v}"));
        return $"option name {Name} type combo default {DefaultValue} {vars}";
    }

    public override bool TrySetValue(string? value)
    {
        if (value != null && VarOptions.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            Value = value;
            return true;
        }
        return false;
    }

    public override string GetCurrentValueString() => _value;
}

public sealed class UciOptionRegistry
{
    private readonly Dictionary<string, UciOption> _options = new(StringComparer.OrdinalIgnoreCase);

    public SpinOption Hash { get; }
    public SpinOption Threads { get; }
    public CheckOption OwnBook { get; }
    public StringOption BookFile { get; }
    public ComboOption BookMode { get; }
    public StringOption SyzygyPath { get; }
    public SpinOption SyzygyProbeDepth { get; }
    public CheckOption UseNnue { get; }
    public StringOption NnuePath { get; }
    public StringOption SearchTraceFile { get; }
    public SpinOption LmrBase { get; }
    public SpinOption LmrDivision { get; }
    public SpinOption RfpMargin { get; }
    public SpinOption NmpBase { get; }
    public SpinOption AspirationDelta { get; }
    public SpinOption ConthistReward { get; }
    public SpinOption ConthistPenalty { get; }
    public SpinOption HistoryThreshold { get; }
    public SpinOption ScoreDropThreshold { get; }
    public SpinOption RfpImprovingMultiplier { get; }
    public SpinOption SingularMargin { get; }
    public SpinOption QsDeltaMargin { get; }
    public SpinOption HistoryBonusScale { get; }
    public SpinOption HistoryMaxBonus { get; }
    public SpinOption HistoryMaxMalus { get; }
    public SpinOption CorrHistDivisor { get; }
    public SpinOption CorrHistMax { get; }
    public SpinOption FutilityMarginD1D2 { get; }
    public SpinOption FutilityMarginD3 { get; }
    public SpinOption LmpBase { get; }
    public SpinOption LmpMultiplier { get; }
    public SpinOption SeeCaptureMargin { get; }
    public SpinOption SeeQuietMargin { get; }
    public SpinOption MoveOverhead { get; }
    public SpinOption TimeMovesToGo { get; }
    public SpinOption TimeIncFactor { get; }
    public SpinOption TimeHardMultiplier { get; }
    public SpinOption TimeSoftHardRatio { get; }
    public SpinOption TimeFlipScale { get; }
    public SpinOption TimeStableScale { get; }
    public SpinOption TimeScoreDropScale { get; }
    public SpinOption TimeFailLowScale { get; }
    public StringOption ExpertRouteOrder { get; }
    public SpinOption ExpertEndgameMaxPieces { get; }
    public SpinOption ExpertImbalanceMargin { get; }
    public SpinOption ExpertOpenThreshold { get; }
    public SpinOption ExpertAttackMinOpenness { get; }

    public UciOptionRegistry()
    {
        SearchTraceFile = new StringOption("SearchTraceFile", defaultValue: string.Empty);
        Hash = new SpinOption("Hash", defaultValue: 64, min: 1, max: 4194304);
        Threads = new SpinOption("Threads", defaultValue: 1, min: 1, max: 1024);
        OwnBook = new CheckOption("OwnBook", defaultValue: true);
        BookFile = new StringOption("BookFile", defaultValue: "book.bin");
        BookMode = new ComboOption("BookMode", defaultValue: "WeightedRandom", ["WeightedRandom", "BestMove", "UniformRandom"]);
        SyzygyPath = new StringOption("SyzygyPath", defaultValue: string.Empty);
        SyzygyProbeDepth = new SpinOption("SyzygyProbeDepth", defaultValue: 1, min: 1, max: 100);
        UseNnue = new CheckOption("UseNnue", defaultValue: true);
        NnuePath = new StringOption("NnuePath", defaultValue: "./networks");
        LmrBase = new SpinOption("LmrBase", defaultValue: 83, min: 0, max: 250);
        LmrDivision = new SpinOption("LmrDivision", defaultValue: 228, min: 50, max: 500);
        RfpMargin = new SpinOption("RfpMargin", defaultValue: 74, min: 10, max: 300);
        NmpBase = new SpinOption("NmpBase", defaultValue: 3, min: 1, max: 10);
        AspirationDelta = new SpinOption("AspirationDelta", defaultValue: 19, min: 5, max: 100);
        ConthistReward = new SpinOption("ConthistReward", defaultValue: 2611, min: 500, max: 6000);
        ConthistPenalty = new SpinOption("ConthistPenalty", defaultValue: 3805, min: 1000, max: 8000);
        HistoryThreshold = new SpinOption("HistoryThreshold", defaultValue: 2139, min: 500, max: 5000);
        ScoreDropThreshold = new SpinOption("ScoreDropThreshold", defaultValue: 45, min: 10, max: 150);
        RfpImprovingMultiplier = new SpinOption("RfpImprovingMultiplier", defaultValue: 111, min: 100, max: 250);
        SingularMargin = new SpinOption("SingularMargin", defaultValue: 6, min: 1, max: 8);
        QsDeltaMargin = new SpinOption("QsDeltaMargin", defaultValue: 188, min: 50, max: 500);
        HistoryBonusScale = new SpinOption("HistoryBonusScale", defaultValue: 16, min: 1, max: 64);
        HistoryMaxBonus = new SpinOption("HistoryMaxBonus", defaultValue: 2000, min: 200, max: 5000);
        HistoryMaxMalus = new SpinOption("HistoryMaxMalus", defaultValue: 2000, min: 200, max: 5000);
        CorrHistDivisor = new SpinOption("CorrHistDivisor", defaultValue: SearchHeuristics.TunedCorrHistDivisor, min: 256, max: 8192);
        CorrHistMax = new SpinOption("CorrHistMax", defaultValue: SearchHeuristics.TunedCorrHistMax, min: 16, max: 256);
        FutilityMarginD1D2 = new SpinOption("FutilityMarginD1D2", defaultValue: SearchHeuristics.TunedFutilityMarginD1D2, min: 50, max: 350);
        FutilityMarginD3 = new SpinOption("FutilityMarginD3", defaultValue: SearchHeuristics.TunedFutilityMarginD3, min: 30, max: 300);
        LmpBase = new SpinOption("LmpBase", defaultValue: SearchHeuristics.TunedLmpBase, min: 1, max: 12);
        LmpMultiplier = new SpinOption("LmpMultiplier", defaultValue: SearchHeuristics.TunedLmpMultiplier, min: 1, max: 6);
        SeeCaptureMargin = new SpinOption("SeeCaptureMargin", defaultValue: SearchHeuristics.TunedSeeCaptureMargin, min: 10, max: 200);
        SeeQuietMargin = new SpinOption("SeeQuietMargin", defaultValue: SearchHeuristics.TunedSeeQuietMargin, min: 10, max: 150);
        MoveOverhead = new SpinOption("Move Overhead", defaultValue: TimeManager.SafetyLagMs, min: 0, max: 5000);
        TimeMovesToGo = new SpinOption("TimeMovesToGo", defaultValue: 38, min: 15, max: 65);
        TimeIncFactor = new SpinOption("TimeIncFactor", defaultValue: 73, min: 20, max: 100);
        TimeHardMultiplier = new SpinOption("TimeHardMultiplier", defaultValue: 181, min: 110, max: 260);
        TimeSoftHardRatio = new SpinOption("TimeSoftHardRatio", defaultValue: 60, min: 40, max: 80);
        TimeFlipScale = new SpinOption("TimeFlipScale", defaultValue: 150, min: 105, max: 250);
        TimeStableScale = new SpinOption("TimeStableScale", defaultValue: 75, min: 40, max: 100);
        TimeScoreDropScale = new SpinOption("TimeScoreDropScale", defaultValue: 131, min: 105, max: 220);
        TimeFailLowScale = new SpinOption("TimeFailLowScale", defaultValue: 140, min: 105, max: 230);

        // NNUE expert routing (tunable; defaults set to SPSA tuned parameters)
        ExpertRouteOrder = new StringOption("ExpertRouteOrder", defaultValue: "EQIA");
        ExpertEndgameMaxPieces = new SpinOption("ExpertEndgameMaxPieces", defaultValue: 16, min: 6, max: 24);
        ExpertImbalanceMargin = new SpinOption("ExpertImbalanceMargin", defaultValue: 4, min: 1, max: 6);
        ExpertOpenThreshold = new SpinOption("ExpertOpenThreshold", defaultValue: 49, min: 20, max: 80);
        ExpertAttackMinOpenness = new SpinOption("ExpertAttackMinOpenness", defaultValue: 3, min: 0, max: 100);

        Register(SearchTraceFile);
        Register(Hash);
        Register(Threads);
        Register(OwnBook);
        Register(BookFile);
        Register(BookMode);
        Register(SyzygyPath);
        Register(SyzygyProbeDepth);
        Register(UseNnue);
        Register(NnuePath);
        Register(LmrBase);
        Register(LmrDivision);
        Register(RfpMargin);
        Register(NmpBase);
        Register(AspirationDelta);
        Register(ConthistReward);
        Register(ConthistPenalty);
        Register(HistoryThreshold);
        Register(ScoreDropThreshold);
        Register(RfpImprovingMultiplier);
        Register(SingularMargin);
        Register(QsDeltaMargin);
        Register(HistoryBonusScale);
        Register(HistoryMaxBonus);
        Register(HistoryMaxMalus);
        Register(CorrHistDivisor);
        Register(CorrHistMax);
        Register(FutilityMarginD1D2);
        Register(FutilityMarginD3);
        Register(LmpBase);
        Register(LmpMultiplier);
        Register(SeeCaptureMargin);
        Register(SeeQuietMargin);
        Register(MoveOverhead);
        Register(TimeMovesToGo);
        Register(TimeIncFactor);
        Register(TimeHardMultiplier);
        Register(TimeSoftHardRatio);
        Register(TimeFlipScale);
        Register(TimeStableScale);
        Register(TimeScoreDropScale);
        Register(TimeFailLowScale);
        Register(ExpertRouteOrder);
        Register(ExpertEndgameMaxPieces);
        Register(ExpertImbalanceMargin);
        Register(ExpertOpenThreshold);
        Register(ExpertAttackMinOpenness);
    }

    public void Register(UciOption option)
    {
        _options[option.Name] = option;
    }

    public bool TryGet(string name, out UciOption? option) =>
        _options.TryGetValue(name, out option);

    public bool TrySetValue(string name, string? value)
    {
        if (_options.TryGetValue(name, out var option))
        {
            return option.TrySetValue(value);
        }
        return false;
    }

    public IEnumerable<UciOption> All => _options.Values;

    public IEnumerable<string> GetDeclarations()
    {
        foreach (var opt in _options.Values)
        {
            yield return opt.GetUciDeclaration();
        }
    }
}
