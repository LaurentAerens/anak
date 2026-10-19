using System.Runtime.InteropServices;

namespace Anak.Core;

[StructLayout(LayoutKind.Sequential)]
public readonly struct BoardState
{
    public readonly CastlingRights Castling;
    public readonly Square EpSquare;
    public readonly ushort HalfmoveClock;
    public readonly Piece CapturedPiece;
    public readonly ulong ZobristKey;

    public BoardState(CastlingRights castling, Square epSquare, ushort halfmoveClock, Piece capturedPiece, ulong zobristKey)
    {
        Castling = castling;
        EpSquare = epSquare;
        HalfmoveClock = halfmoveClock;
        CapturedPiece = capturedPiece;
        ZobristKey = zobristKey;
    }
}
