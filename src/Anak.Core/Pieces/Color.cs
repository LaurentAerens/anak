namespace Anak.Core;

public enum Color : byte
{
    White = 0,
    Black = 1
}

public static class ColorExtensions
{
    public static Color Opponent(this Color color) => (Color)((byte)color ^ 1);
}
