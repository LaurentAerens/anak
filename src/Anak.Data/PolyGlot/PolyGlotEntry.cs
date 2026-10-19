using System.Runtime.InteropServices;
using Anak.Core;

namespace Anak.Data.PolyGlot;

/// <summary>
/// A 16-byte packed PolyGlot opening book entry.
/// Standard binary layout:
/// - 8 bytes: 64-bit Zobrist key (big-endian on disk)
/// - 2 bytes: 16-bit move encoding (big-endian on disk)
/// - 2 bytes: 16-bit weight / frequency (big-endian on disk)
/// - 4 bytes: 32-bit learn / engine score (big-endian on disk)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct PolyGlotEntry : IComparable<PolyGlotEntry>, IEquatable<PolyGlotEntry>
{
    public readonly ulong Key;
    public readonly ushort Move;
    public readonly ushort Weight;
    public readonly uint Learn;

    public PolyGlotEntry(ulong key, ushort move, ushort weight, uint learn)
    {
        Key = key;
        Move = move;
        Weight = weight;
        Learn = learn;
    }

    /// <summary>
    /// Destination file index (0 = A, 7 = H).
    /// </summary>
    public int ToFile => (Move >> 0) & 7;

    /// <summary>
    /// Destination rank index (0 = 1, 7 = 8).
    /// </summary>
    public int ToRank => (Move >> 3) & 7;

    /// <summary>
    /// Origin file index (0 = A, 7 = H).
    /// </summary>
    public int FromFile => (Move >> 6) & 7;

    /// <summary>
    /// Origin rank index (0 = 1, 7 = 8).
    /// </summary>
    public int FromRank => (Move >> 9) & 7;

    /// <summary>
    /// Promotion piece code: 0 = none, 1 = knight, 2 = bishop, 3 = rook, 4 = queen.
    /// </summary>
    public int PromotionPiece => (Move >> 12) & 7;

    /// <summary>
    /// Origin square.
    /// </summary>
    public Square FromSquare => SquareExtensions.Create(FromFile, FromRank);

    /// <summary>
    /// Destination square.
    /// </summary>
    public Square ToSquare => SquareExtensions.Create(ToFile, ToRank);

    public int CompareTo(PolyGlotEntry other) => Key.CompareTo(other.Key);

    public bool Equals(PolyGlotEntry other) =>
        Key == other.Key && Move == other.Move && Weight == other.Weight && Learn == other.Learn;

    public override bool Equals(object? obj) => obj is PolyGlotEntry other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Move, Weight, Learn);

    public static bool operator ==(PolyGlotEntry left, PolyGlotEntry right) => left.Equals(right);
    public static bool operator !=(PolyGlotEntry left, PolyGlotEntry right) => !left.Equals(right);

    public override string ToString() =>
        $"Key: 0x{Key:X16}, From: {FromSquare}, To: {ToSquare}, Promo: {PromotionPiece}, Weight: {Weight}, Learn: {Learn}";
}
