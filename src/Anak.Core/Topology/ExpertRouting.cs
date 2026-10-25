namespace Anak.Core;

/// <summary>
/// Tunable, immutable configuration for <see cref="PawnTopology.ClassifyExpert(in Board, ExpertRouting)"/>.
/// Controls which override rules are active, the order in which they are evaluated (first match wins),
/// and the numeric thresholds each rule uses. The Open/Closed split is always the final fallback.
/// <para>
/// <see cref="Default"/> reproduces the original hard-coded routing exactly
/// (order <c>EQIA</c>, 14 pieces, margin 2, open threshold 0.50, no attack openness gate).
/// </para>
/// </summary>
public sealed class ExpertRouting
{
    /// <summary>Rule code for <see cref="NnueExpertType.Endgame"/>.</summary>
    public const char EndgameRule = 'E';

    /// <summary>Rule code for <see cref="NnueExpertType.Queenless"/>.</summary>
    public const char QueenlessRule = 'Q';

    /// <summary>Rule code for <see cref="NnueExpertType.Imbalance"/>.</summary>
    public const char ImbalanceRule = 'I';

    /// <summary>Rule code for <see cref="NnueExpertType.Attack"/>.</summary>
    public const char AttackRule = 'A';

    /// <summary>Default rule order string.</summary>
    public const string DefaultOrder = "EQIA";

    /// <summary>
    /// Tuned routing configuration obtained via SPSA match optimization:
    /// Order EQIA, Endgame pieces &lt;= 16, Imbalance margin &gt;= 4, Open threshold &gt;= 0.49, Attack min openness &gt;= 0.03.
    /// </summary>
    public static readonly ExpertRouting Default = new(
        order: DefaultOrder,
        endgameMaxPieces: 16,
        imbalanceMargin: 4,
        openThreshold: 0.49f,
        attackMinOpenness: 0.03f);

    private readonly char[] _order;

    /// <summary>Creates a routing configuration. Invalid <paramref name="order"/> falls back to <see cref="DefaultOrder"/>.</summary>
    /// <param name="order">Rule evaluation order; each of E, Q, I, A at most once. Omitted rules are disabled.</param>
    /// <param name="endgameMaxPieces">Endgame applies when total piece count is at most this value.</param>
    /// <param name="imbalanceMargin">Imbalance applies when material differs by at least this many points.</param>
    /// <param name="openThreshold">Openness at or above this value routes to Open, otherwise Closed (0..1).</param>
    /// <param name="attackMinOpenness">
    /// Attack additionally requires openness at or above this value (0 disables the gate).
    /// Opposite-flank positions below it fall through to the remaining rules, typically ending up Closed.
    /// </param>
    public ExpertRouting(
        string order = DefaultOrder,
        int endgameMaxPieces = 16,
        int imbalanceMargin = 4,
        float openThreshold = 0.49f,
        float attackMinOpenness = 0.03f)
    {
        _order = TryParseOrder(order, out char[]? parsed) ? parsed! : DefaultOrder.ToCharArray();
        Order = new string(_order);
        EndgameMaxPieces = endgameMaxPieces;
        ImbalanceMargin = imbalanceMargin;
        OpenThreshold = openThreshold;
        AttackMinOpenness = attackMinOpenness;
    }

    /// <summary>Normalized (upper-case) rule order string.</summary>
    public string Order { get; }

    public int EndgameMaxPieces { get; }

    public int ImbalanceMargin { get; }

    public float OpenThreshold { get; }

    public float AttackMinOpenness { get; }

    /// <summary>Rule codes in evaluation order.</summary>
    public ReadOnlySpan<char> Rules => _order;

    /// <summary>
    /// Validates and normalizes a rule order string: only E, Q, I, A (case-insensitive), no duplicates.
    /// An empty string is valid and disables all override rules.
    /// </summary>
    public static bool TryParseOrder(string? order, out char[]? rules)
    {
        rules = null;
        if (order == null)
        {
            return false;
        }

        var result = new char[order.Length];
        int seen = 0;
        for (int i = 0; i < order.Length; i++)
        {
            char c = char.ToUpperInvariant(order[i]);
            int bit = c switch
            {
                EndgameRule => 1,
                QueenlessRule => 2,
                ImbalanceRule => 4,
                AttackRule => 8,
                _ => 0
            };

            if (bit == 0 || (seen & bit) != 0)
            {
                return false;
            }

            seen |= bit;
            result[i] = c;
        }

        rules = result;
        return true;
    }

    /// <summary>Returns every valid ordering of the four rules (24 permutations), for exhaustive order search.</summary>
    public static IReadOnlyList<string> AllFullOrders()
    {
        var list = new List<string>();
        Permute("EQIA".ToCharArray(), 0, list);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static void Permute(char[] a, int k, List<string> output)
    {
        if (k == a.Length)
        {
            output.Add(new string(a));
            return;
        }

        for (int i = k; i < a.Length; i++)
        {
            (a[k], a[i]) = (a[i], a[k]);
            Permute(a, k + 1, output);
            (a[k], a[i]) = (a[i], a[k]);
        }
    }

    public override string ToString() =>
        $"order={Order} endgameMaxPieces={EndgameMaxPieces} imbalanceMargin={ImbalanceMargin} " +
        $"openThreshold={OpenThreshold:F2} attackMinOpenness={AttackMinOpenness:F2}";
}
