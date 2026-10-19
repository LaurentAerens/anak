using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Anak.Core;

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public readonly struct Move : IEquatable<Move>, IComparable<Move>
{
    private readonly ushort _data;

    public static readonly Move Null = default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Move(Square from, Square to, MoveType type = MoveType.Quiet)
    {
        _data = (ushort)((byte)from | ((byte)to << 6) | ((ushort)type << 12));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Move(ushort data)
    {
        _data = data;
    }

    public ushort RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _data;
    }

    public Square From
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Square)(_data & 0x3F);
    }

    public Square To
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Square)((_data >> 6) & 0x3F);
    }

    public MoveType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (MoveType)(_data >> 12);
    }

    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _data == 0;
    }

    public bool IsCapture
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_data & 0x4000) != 0 || Type == MoveType.EnPassant;
    }

    public bool IsPromotion
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_data & 0x8000) != 0;
    }

    public bool IsCastling
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type is MoveType.KingCastle or MoveType.QueenCastle;
    }

    public bool IsEnPassant
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type == MoveType.EnPassant;
    }

    public PieceType PromotionPieceType
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Type switch
        {
            MoveType.KnightPromotion or MoveType.KnightPromotionCapture => PieceType.Knight,
            MoveType.BishopPromotion or MoveType.BishopPromotionCapture => PieceType.Bishop,
            MoveType.RookPromotion or MoveType.RookPromotionCapture => PieceType.Rook,
            MoveType.QueenPromotion or MoveType.QueenPromotionCapture => PieceType.Queen,
            _ => PieceType.None
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Move other) => _data == other._data;

    public override bool Equals(object? obj) => obj is Move other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _data;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(Move other) => _data.CompareTo(other._data);

    public static bool operator ==(Move left, Move right) => left._data == right._data;
    public static bool operator !=(Move left, Move right) => left._data != right._data;

    public string ToUci()
    {
        if (IsNull) return "0000";
        var fromStr = From.Name();
        var toStr = To.Name();
        var promo = PromotionPieceType switch
        {
            PieceType.Knight => "n",
            PieceType.Bishop => "b",
            PieceType.Rook => "r",
            PieceType.Queen => "q",
            _ => ""
        };
        return $"{fromStr}{toStr}{promo}";
    }

    public override string ToString() => ToUci();

    public static Move ParseUci(ReadOnlySpan<char> uci)
    {
        if (uci.Length < 4) return Null;
        var from = SquareExtensions.Parse(uci[..2]);
        var to = SquareExtensions.Parse(uci.Slice(2, 2));
        if (from == Square.None || to == Square.None) return Null;

        MoveType type = MoveType.Quiet;
        if (uci.Length >= 5)
        {
            type = char.ToLowerInvariant(uci[4]) switch
            {
                'n' => MoveType.KnightPromotion,
                'b' => MoveType.BishopPromotion,
                'r' => MoveType.RookPromotion,
                'q' => MoveType.QueenPromotion,
                _ => MoveType.Quiet
            };
        }
        return new Move(from, to, type);
    }
}
