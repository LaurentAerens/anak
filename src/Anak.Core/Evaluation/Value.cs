using System.Runtime.CompilerServices;

namespace Anak.Core;

public readonly struct Value : IEquatable<Value>, IComparable<Value>
{
    public readonly int Raw;

    public const int MateValue = 30000;
    public const int InfiniteValue = 32000;

    public static readonly Value Zero = new(0);
    public static readonly Value Draw = new(0);
    public static readonly Value Win = new(MateValue);
    public static readonly Value Loss = new(-MateValue);
    public static readonly Value PositiveInfinity = new(InfiniteValue);
    public static readonly Value NegativeInfinity = new(-InfiniteValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Value(int raw) => Raw = raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator int(Value v) => v.Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator Value(int raw) => new(raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value operator -(Value v) => new(-v.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value operator +(Value a, Value b) => new(a.Raw + b.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value operator -(Value a, Value b) => new(a.Raw - b.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Value left, Value right) => left.Raw == right.Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Value left, Value right) => left.Raw != right.Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(Value left, Value right) => left.Raw < right.Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(Value left, Value right) => left.Raw <= right.Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(Value left, Value right) => left.Raw > right.Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(Value left, Value right) => left.Raw >= right.Raw;

    public bool Equals(Value other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Value other && Equals(other);
    public override int GetHashCode() => Raw;
    public int CompareTo(Value other) => Raw.CompareTo(other.Raw);

    public override string ToString() => Raw.ToString();
}
