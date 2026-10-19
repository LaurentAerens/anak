using System.Runtime.CompilerServices;

namespace Anak.Core;

public static class Zobrist
{
    public static readonly ulong[,] PieceSquare = new ulong[15, 64];
    public static readonly ulong BlackToMove;
    public static readonly ulong[] Castling = new ulong[16];
    public static readonly ulong[] EpFile = new ulong[8];

    static Zobrist()
    {
        // Fixed seed PRNG (SplitMix64) for 100% deterministic, high-quality Zobrist keys
        ulong seed = 0x98F1070372180420UL;

        ulong NextRandom()
        {
            seed += 0x9E3779B97F4A7C15UL;
            ulong z = seed;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        for (int piece = 0; piece < 15; piece++)
        {
            for (int sq = 0; sq < 64; sq++)
            {
                PieceSquare[piece, sq] = NextRandom();
            }
        }

        BlackToMove = NextRandom();

        for (int i = 0; i < 16; i++)
        {
            Castling[i] = NextRandom();
        }

        for (int f = 0; f < 8; f++)
        {
            EpFile[f] = NextRandom();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ComputeKey(in Board board)
    {
        ulong key = 0UL;

        ulong occupied = board.Occupied;
        while (occupied != 0)
        {
            Square sq = Bitboard.PopLsb(ref occupied);
            Piece piece = board.GetPieceAt(sq);
            if (piece != Piece.None)
            {
                key ^= PieceSquare[(int)piece, (int)sq];
            }
        }

        if (board.SideToMove == Color.Black)
        {
            key ^= BlackToMove;
        }

        key ^= Castling[(int)board.Castling];

        if (board.EpSquare != Square.None)
        {
            key ^= EpFile[(int)board.EpSquare.File()];
        }

        return key;
    }
}
