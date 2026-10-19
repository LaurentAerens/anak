namespace Anak.Core;

public enum Square : byte
{
    A1 = 0, B1 = 1, C1 = 2, D1 = 3, E1 = 4, F1 = 5, G1 = 6, H1 = 7,
    A2 = 8, B2 = 9, C2 = 10, D2 = 11, E2 = 12, F2 = 13, G2 = 14, H2 = 15,
    A3 = 16, B3 = 17, C3 = 18, D3 = 19, E3 = 20, F3 = 21, G3 = 22, H3 = 23,
    A4 = 24, B4 = 25, C4 = 26, D4 = 27, E4 = 28, F4 = 29, G4 = 30, H4 = 31,
    A5 = 32, B5 = 33, C5 = 34, D5 = 35, E5 = 36, F5 = 37, G5 = 38, H5 = 39,
    A6 = 40, B6 = 41, C6 = 42, D6 = 43, E6 = 44, F6 = 45, G6 = 46, H6 = 47,
    A7 = 48, B7 = 49, C7 = 50, D7 = 51, E7 = 52, F7 = 53, G7 = 54, H7 = 55,
    A8 = 56, B8 = 57, C8 = 58, D8 = 59, E8 = 60, F8 = 61, G8 = 62, H8 = 63,
    None = 64
}

public static class SquareExtensions
{
    public static File File(this Square sq) => (File)((byte)sq & 7);
    public static Rank Rank(this Square sq) => (Rank)((byte)sq >> 3);

    public static Square Create(File file, Rank rank) => (Square)(((byte)rank << 3) | (byte)file);
    public static Square Create(int file, int rank) => (Square)((rank << 3) | file);

    public static string Name(this Square sq)
    {
        if (sq == Square.None || (byte)sq >= 64) return "-";
        return $"{(char)('a' + (int)sq.File())}{(char)('1' + (int)sq.Rank())}";
    }

    public static Square Parse(ReadOnlySpan<char> span)
    {
        if (span.Length < 2 || span[0] == '-') return Square.None;
        int file = span[0] - 'a';
        int rank = span[1] - '1';
        if (file is < 0 or > 7 || rank is < 0 or > 7) return Square.None;
        return Create(file, rank);
    }
}
