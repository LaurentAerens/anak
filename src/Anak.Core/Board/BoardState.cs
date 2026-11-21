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
    public readonly ulong PawnKey;
    public readonly ulong WhiteNonPawnKey;
    public readonly ulong BlackNonPawnKey;

    public BoardState(CastlingRights castling, Square epSquare, ushort halfmoveClock, Piece capturedPiece, ulong zobristKey, ulong pawnKey = 0, ulong whiteNonPawnKey = 0, ulong blackNonPawnKey = 0)
    {
        Castling = castling;
        EpSquare = epSquare;
        HalfmoveClock = halfmoveClock;
        CapturedPiece = capturedPiece;
        ZobristKey = zobristKey;
        PawnKey = pawnKey;
        WhiteNonPawnKey = whiteNonPawnKey;
        BlackNonPawnKey = blackNonPawnKey;
    }
}
