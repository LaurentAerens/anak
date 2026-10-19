using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Anak.Core;

public static class MagicBitboards
{
    public static readonly ulong[] BishopMasks = new ulong[64];
    public static readonly ulong[] RookMasks = new ulong[64];

    public static readonly int[] BishopShifts = new int[64];
    public static readonly int[] RookShifts = new int[64];

    public static readonly int[] BishopOffsets = new int[64];
    public static readonly int[] RookOffsets = new int[64];

    public static readonly ulong[] BishopMagics = [
        0x08102000C4044043UL, 0x0A200218C5110010UL, 0x2104A10A02014401UL, 0x8048204441001000UL,
        0x0002021010009001UL, 0x0806866020025101UL, 0x0001040202420001UL, 0x0003004104200200UL,
        0x0041100288084480UL, 0x2004117408014044UL, 0x0000080094048000UL, 0x2040044400808000UL,
        0x5050840421100020UL, 0x00200201D0280011UL, 0x0008204410051080UL, 0x2000010400820840UL,
        0x900BA01020010400UL, 0x0084212208A80100UL, 0x0904018200220202UL, 0x0004000840400802UL,
        0x0844002822080010UL, 0x0080200200900800UL, 0xA0011A0403081AC0UL, 0x0081004824061208UL,
        0x0020128020841101UL, 0x0004021010301100UL, 0x201C084084080421UL, 0x2644040010401080UL,
        0x000101000010400AUL, 0x0000441006010400UL, 0x0210948804040402UL, 0x2102002000490800UL,
        0x0004022094482101UL, 0x8081106808824800UL, 0xC014841000010044UL, 0x051A820080880080UL,
        0x8000620020420080UL, 0x2404030602044800UL, 0x6210020060088400UL, 0xA104008600088044UL,
        0x1208020290202220UL, 0x0042450410092082UL, 0x0911008040420400UL, 0x6140022018098100UL,
        0x2011011020800404UL, 0x1820021042020842UL, 0x0132901200830218UL, 0x0081040108400201UL,
        0x14009C0128402000UL, 0x060683A988200300UL, 0x0009420100886402UL, 0x0000088884041010UL,
        0x00090090021210C0UL, 0x8258080228021440UL, 0x00371802040408A0UL, 0x002081021089020AUL,
        0x8042024210902841UL, 0x4008010042100400UL, 0x0090000021282804UL, 0x0A0004004104880AUL,
        0x3000201084050404UL, 0x4090020810500690UL, 0x2020100501042400UL, 0x00103000C8004040UL,
    ];

    public static readonly ulong[] RookMagics = [
        0x6080008020104000UL, 0x0040200010004002UL, 0x010020004011000AUL, 0x0500050020100008UL,
        0x1500100500420800UL, 0x0300110002080400UL, 0x0480220000800100UL, 0x02000240820D0824UL,
        0x0000800080204004UL, 0x0200400040201002UL, 0x0008808010002000UL, 0x0200801000800804UL,
        0x0010808008008400UL, 0x0102000200041108UL, 0x0801000401000200UL, 0x002200008104284AUL,
        0x8580208008904000UL, 0x0200464000201008UL, 0x0000828010002000UL, 0x9004420008120022UL,
        0x0300808004000800UL, 0x0902008002800400UL, 0x0850040008108201UL, 0x0000220014488104UL,
        0x40800040C0012010UL, 0x2A40008480200042UL, 0x6001001100402000UL, 0x0010008080080012UL,
        0x4000080080800400UL, 0x0102020080040080UL, 0x0002821400900138UL, 0x300F204200008104UL,
        0x0180004000402008UL, 0x8800200080804004UL, 0x0080801000802004UL, 0x5040800800801001UL,
        0x0000040080800800UL, 0x0054800400800200UL, 0x0108488204001001UL, 0x8000108062000401UL,
        0x0058802040088009UL, 0x0210004020004001UL, 0x0090040028002000UL, 0x2300090010010020UL,
        0x0014008040080800UL, 0x0022000408020010UL, 0x0121000200010004UL, 0xA400804400820001UL,
        0x0000A24080150100UL, 0x4200804000200080UL, 0x1002001020804200UL, 0xC204100302A00900UL,
        0x02108048001C0180UL, 0x0086800400020080UL, 0x5038011002080400UL, 0x0000051058841200UL,
        0x0201028000106941UL, 0x0001004020801202UL, 0x04001100C1200039UL, 0x0204040900201001UL,
        0x800600200834508AUL, 0x1051000400080203UL, 0x0002000804016082UL, 0x0080840080410022UL,
    ];

    private static readonly ulong[] _bishopTable = new ulong[5248];
    private static readonly ulong[] _rookTable = new ulong[102400];

    static MagicBitboards()
    {
        // 1. Compute masks, shifts, and table offsets
        int bishopOffset = 0;
        int rookOffset = 0;

        for (int sq = 0; sq < 64; sq++)
        {
            BishopOffsets[sq] = bishopOffset;
            ulong bMask = GenerateBishopMask(sq);
            BishopMasks[sq] = bMask;
            int bBits = BitOperations.PopCount(bMask);
            BishopShifts[sq] = 64 - bBits;
            bishopOffset += 1 << bBits;

            RookOffsets[sq] = rookOffset;
            ulong rMask = GenerateRookMask(sq);
            RookMasks[sq] = rMask;
            int rBits = BitOperations.PopCount(rMask);
            RookShifts[sq] = 64 - rBits;
            rookOffset += 1 << rBits;
        }

        // 2. Populate attack tables
        for (int sq = 0; sq < 64; sq++)
        {
            PopulateTable(sq, isRook: false);
            PopulateTable(sq, isRook: true);
        }
    }

    private static void PopulateTable(int sq, bool isRook)
    {
        ulong mask = isRook ? RookMasks[sq] : BishopMasks[sq];
        int shift = isRook ? RookShifts[sq] : BishopShifts[sq];
        int offset = isRook ? RookOffsets[sq] : BishopOffsets[sq];
        ulong magic = isRook ? RookMagics[sq] : BishopMagics[sq];
        ulong[] table = isRook ? _rookTable : _bishopTable;

        int bits = 64 - shift;
        int numSubsets = 1 << bits;

        for (uint i = 0; i < numSubsets; i++)
        {
            ulong occ = 0UL;
            ulong tempMask = mask;
            for (int b = 0; b < bits; b++)
            {
                int lsb = BitOperations.TrailingZeroCount(tempMask);
                if ((i & (1u << b)) != 0) occ |= 1UL << lsb;
                tempMask &= tempMask - 1;
            }

            ulong attacks = isRook ? ComputeRookAttacks(sq, occ) : ComputeBishopAttacks(sq, occ);

            if (Bmi2.X64.IsSupported)
            {
                int pextIndex = (int)Bmi2.X64.ParallelBitExtract(occ, mask);
                table[offset + pextIndex] = attacks;
            }
            else
            {
                int magicIndex = (int)((occ * magic) >> shift);
                table[offset + magicIndex] = attacks;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetBishopAttacks(Square square, ulong occupancy)
    {
        int sq = (byte)square;
        ulong mask = BishopMasks[sq];
        int offset = BishopOffsets[sq];

        if (Bmi2.X64.IsSupported)
        {
            int index = (int)Bmi2.X64.ParallelBitExtract(occupancy, mask);
            return _bishopTable[offset + index];
        }
        else
        {
            ulong occ = occupancy & mask;
            int index = (int)((occ * BishopMagics[sq]) >> BishopShifts[sq]);
            return _bishopTable[offset + index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetRookAttacks(Square square, ulong occupancy)
    {
        int sq = (byte)square;
        ulong mask = RookMasks[sq];
        int offset = RookOffsets[sq];

        if (Bmi2.X64.IsSupported)
        {
            int index = (int)Bmi2.X64.ParallelBitExtract(occupancy, mask);
            return _rookTable[offset + index];
        }
        else
        {
            ulong occ = occupancy & mask;
            int index = (int)((occ * RookMagics[sq]) >> RookShifts[sq]);
            return _rookTable[offset + index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetQueenAttacks(Square square, ulong occupancy) =>
        GetBishopAttacks(square, occupancy) | GetRookAttacks(square, occupancy);

    public static ulong GenerateBishopMask(int sq)
    {
        ulong mask = 0UL;
        int r = sq >> 3, f = sq & 7;
        for (int dr = -1; dr <= 1; dr += 2)
        {
            for (int df = -1; df <= 1; df += 2)
            {
                for (int nr = r + dr, nf = f + df; nr >= 1 && nr <= 6 && nf >= 1 && nf <= 6; nr += dr, nf += df)
                {
                    mask |= 1UL << ((nr << 3) | nf);
                }
            }
        }
        return mask;
    }

    public static ulong GenerateRookMask(int sq)
    {
        ulong mask = 0UL;
        int r = sq >> 3, f = sq & 7;
        for (int nr = r + 1; nr <= 6; nr++) mask |= 1UL << ((nr << 3) | f);
        for (int nr = r - 1; nr >= 1; nr--) mask |= 1UL << ((nr << 3) | f);
        for (int nf = f + 1; nf <= 6; nf++) mask |= 1UL << ((r << 3) | nf);
        for (int nf = f - 1; nf >= 1; nf--) mask |= 1UL << ((r << 3) | nf);
        return mask;
    }

    public static ulong ComputeBishopAttacks(int sq, ulong occ)
    {
        ulong attacks = 0UL;
        int r = sq >> 3, f = sq & 7;
        ReadOnlySpan<(int dr, int df)> dirs = [(-1, -1), (-1, 1), (1, -1), (1, 1)];
        foreach (var (dr, df) in dirs)
        {
            for (int nr = r + dr, nf = f + df; nr >= 0 && nr < 8 && nf >= 0 && nf < 8; nr += dr, nf += df)
            {
                ulong bit = 1UL << ((nr << 3) | nf);
                attacks |= bit;
                if ((occ & bit) != 0) break;
            }
        }
        return attacks;
    }

    public static ulong ComputeRookAttacks(int sq, ulong occ)
    {
        ulong attacks = 0UL;
        int r = sq >> 3, f = sq & 7;
        ReadOnlySpan<(int dr, int df)> dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        foreach (var (dr, df) in dirs)
        {
            for (int nr = r + dr, nf = f + df; nr >= 0 && nr < 8 && nf >= 0 && nf < 8; nr += dr, nf += df)
            {
                ulong bit = 1UL << ((nr << 3) | nf);
                attacks |= bit;
                if ((occ & bit) != 0) break;
            }
        }
        return attacks;
    }

    /// <summary>
    /// Searches for a collision-free magic number using xorshift64 PRNG with sparse randoms.
    /// </summary>
    public static ulong FindMagic(int sq, bool isRook, ref ulong prngSeed)
    {
        ulong mask = isRook ? GenerateRookMask(sq) : GenerateBishopMask(sq);
        int bits = BitOperations.PopCount(mask);
        int numSubsets = 1 << bits;
        int shift = 64 - bits;

        ulong[] occs = new ulong[numSubsets];
        ulong[] atts = new ulong[numSubsets];

        for (int i = 0; i < numSubsets; i++)
        {
            ulong occ = 0UL;
            ulong tempMask = mask;
            for (int b = 0; b < bits; b++)
            {
                int lsb = BitOperations.TrailingZeroCount(tempMask);
                if ((i & (1 << b)) != 0) occ |= 1UL << lsb;
                tempMask &= tempMask - 1;
            }
            occs[i] = occ;
            atts[i] = isRook ? ComputeRookAttacks(sq, occ) : ComputeBishopAttacks(sq, occ);
        }

        ulong[] used = new ulong[numSubsets];
        while (true)
        {
            prngSeed ^= prngSeed << 13;
            prngSeed ^= prngSeed >> 7;
            prngSeed ^= prngSeed << 17;
            ulong r1 = prngSeed;

            prngSeed ^= prngSeed << 13;
            prngSeed ^= prngSeed >> 7;
            prngSeed ^= prngSeed << 17;
            ulong r2 = prngSeed;

            prngSeed ^= prngSeed << 13;
            prngSeed ^= prngSeed >> 7;
            prngSeed ^= prngSeed << 17;
            ulong r3 = prngSeed;

            ulong magic = r1 & r2 & r3;
            if (BitOperations.PopCount((mask * magic) & 0xFF00000000000000UL) < 6) continue;

            Array.Clear(used, 0, numSubsets);
            bool fail = false;
            for (int i = 0; i < numSubsets; i++)
            {
                int idx = (int)((occs[i] * magic) >> shift);
                if (used[idx] == 0)
                {
                    used[idx] = atts[i];
                }
                else if (used[idx] != atts[i])
                {
                    fail = true;
                    break;
                }
            }
            if (!fail) return magic;
        }
    }
}
