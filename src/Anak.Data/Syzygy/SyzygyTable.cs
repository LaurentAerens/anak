using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using Anak.Core;

namespace Anak.Data.Syzygy;

/// <summary>
/// Read-only memory-mapped view over a single .rtbw / .rtbz file.
/// </summary>
internal sealed unsafe class SyzygyMappedFile : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _ptr;

    public long Length { get; }

    public SyzygyMappedFile(string path)
    {
        Length = new FileInfo(path).Length;
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _ptr = p + _view.PointerOffset;
    }

    public int U8(long offset) => _ptr[offset];

    public int U16(long offset) => BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(_ptr + offset, 2));

    public uint U32(long offset) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_ptr + offset, 4));

    public uint U32Be(long offset) => BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(_ptr + offset, 4));

    public ulong U64Be(long offset) => BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(_ptr + offset, 8));

    public bool MagicEquals(ReadOnlySpan<byte> magic) =>
        Length >= magic.Length && new ReadOnlySpan<byte>(_ptr, magic.Length).SequenceEqual(magic);

    public void Dispose()
    {
        if (_ptr != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }
        _view.Dispose();
        _mmf.Dispose();
    }
}

/// <summary>
/// Huffman-pair decoding parameters for one compressed block stream.
/// </summary>
internal sealed class SyzygyPairs
{
    public long IndexTable;
    public long SizeTable;
    public long Data;
    public long Offset;
    public int[] SymLen = [];
    public long SymPat;
    public int BlockSize;
    public int IdxBits;
    public int MinLen;
    public ulong[] Base = [];
}

internal sealed class SyzygyPawnFile
{
    public readonly SyzygyPairs?[] Precomp = new SyzygyPairs?[2];
    public readonly long[][] Factor = new long[2][];
    public readonly int[][] Pieces = new int[2][];
    public readonly int[][] Norm = new int[2][];
}

internal sealed class SyzygyPawnFileDtz
{
    public SyzygyPairs Precomp = null!;
    public long[] Factor = [];
    public int[] Pieces = [];
    public int[] Norm = [];
}

/// <summary>
/// Material-key helpers. A key packs the piece counts of both sides (K,Q,R,B,N,P, 4 bits each),
/// with the first half describing White. The same packing is used for table names and boards.
/// </summary>
internal static class SyzygyKeys
{
    private const string PieceChars = "KQRBNP";

    public static ulong FromCounts(ReadOnlySpan<int> white, ReadOnlySpan<int> black)
    {
        ulong w = 0, b = 0;
        for (int j = 0; j < 6; j++)
        {
            w |= (ulong)white[j] << (4 * j);
            b |= (ulong)black[j] << (4 * j);
        }
        return w | (b << 24);
    }

    public static ulong FromBoard(in Board board)
    {
        Span<int> white = stackalloc int[6];
        Span<int> black = stackalloc int[6];
        for (int pt = 1; pt <= 6; pt++)
        {
            ulong bb = board.GetPieces((PieceType)pt);
            white[6 - pt] = BitOperations.PopCount(bb & board.White);
            black[6 - pt] = BitOperations.PopCount(bb & board.Black);
        }
        return FromCounts(white, black);
    }

    /// <summary>Normalizes a table name the same way python-chess does (stronger side first).</summary>
    public static string NormalizeName(string name, bool mirror)
    {
        int v = name.IndexOf('v');
        string w = Sorted(name[..v]);
        string b = Sorted(name[(v + 1)..]);

        bool swap = mirror ^ (Compare(w.Length, b, b.Length, w) < 0);
        // Python: (len(w), idx(b)) < (len(b), idx(w))
        return swap ? b + "v" + w : w + "v" + b;
    }

    private static int Compare(int lenW, string b, int lenB, string w)
    {
        if (lenW != lenB) return lenW.CompareTo(lenB);
        int n = Math.Min(b.Length, w.Length);
        for (int i = 0; i < n; i++)
        {
            int ib = PieceChars.IndexOf(b[i]);
            int iw = PieceChars.IndexOf(w[i]);
            if (ib != iw) return ib.CompareTo(iw);
        }
        return b.Length.CompareTo(w.Length);
    }

    private static string Sorted(string s)
    {
        char[] arr = s.ToCharArray();
        Array.Sort(arr, (x, y) => PieceChars.IndexOf(x).CompareTo(PieceChars.IndexOf(y)));
        return new string(arr);
    }

    public static ulong FromName(string normalizedName)
    {
        int v = normalizedName.IndexOf('v');
        Span<int> white = stackalloc int[6];
        Span<int> black = stackalloc int[6];
        foreach (char c in normalizedName[..v]) white[PieceChars.IndexOf(c)]++;
        foreach (char c in normalizedName[(v + 1)..]) black[PieceChars.IndexOf(c)]++;
        return FromCounts(white, black);
    }

    /// <summary>
    /// Key actually stored in the file (some tables are stored with a different key than their filename).
    /// <paramref name="pieces"/> holds piece nibbles (type | color &lt;&lt; 3).
    /// </summary>
    public static ulong Recalc(ReadOnlySpan<int> pieces, bool mirror)
    {
        int w = mirror ? 8 : 0;
        int b = mirror ? 0 : 8;
        Span<int> first = stackalloc int[6];
        Span<int> second = stackalloc int[6];
        foreach (int p in pieces)
        {
            for (int j = 0; j < 6; j++)
            {
                int t = 6 - j;
                if (p == (t ^ w)) first[j]++;
                if (p == (t ^ b)) second[j]++;
            }
        }
        return FromCounts(first, second);
    }
}

/// <summary>
/// Combinatorial helpers shared by all tables.
/// </summary>
internal static class SyzygyIndexing
{
    private static readonly long[,] BinomTable = BuildBinom();

    public static readonly long[,] PawnIdx = new long[5, 24];
    public static readonly long[,] PawnFactor = new long[5, 4];

    static SyzygyIndexing()
    {
        for (int i = 0; i < 5; i++)
        {
            int j = 0;
            for (int group = 0; group < 4; group++)
            {
                long s = 0;
                int end = 6 * (group + 1);
                while (j < end)
                {
                    PawnIdx[i, j] = s;
                    s += i == 0 ? 1 : Binom(SyzygyConstants.PTwist[SyzygyConstants.InvFlap[j]], i);
                    j++;
                }
                PawnFactor[i, group] = s;
            }
        }
    }

    private static long[,] BuildBinom()
    {
        var t = new long[65, 65];
        for (int n = 0; n <= 64; n++)
        {
            t[n, 0] = 1;
            for (int k = 1; k <= n; k++)
            {
                t[n, k] = t[n - 1, k - 1] + (k <= n - 1 ? t[n - 1, k] : 0);
            }
        }
        return t;
    }

    public static long Binom(int x, int y) => (x < 0 || y < 0 || y > x || x > 64) ? 0 : BinomTable[x, y];

    public static long SubFactor(int k, int n)
    {
        long f = n;
        long l = 1;
        for (int i = 1; i < k; i++)
        {
            f *= n - i;
            l *= i + 1;
        }
        return f / l;
    }

    public static int OffDiag(int sq) => (sq >> 3) - (sq & 7);

    public static int FlipDiag(int sq) => ((sq >> 3) | (sq << 3)) & 63;
}

/// <summary>
/// Common machinery of Syzygy WDL and DTZ tables: header parsing, position encoding and block decompression.
/// A faithful managed port of the reference implementation (Ronald de Man / python-chess).
/// Only standard-chess encodings (enc types 0 and 2 for piece-only tables, pawn tables) are supported.
/// </summary>
internal abstract class SyzygyTable : IDisposable
{
    protected const int TbPieces = 7;

    private readonly object _initLock = new();
    private volatile bool _initialized;

    protected readonly string Path;
    protected readonly int Num;
    protected readonly bool HasPawns;
    protected readonly int[] Pawns = new int[2];
    protected readonly int EncType;

    protected SyzygyMappedFile Map = null!;
    protected long[] Size = [];
    protected long NextPtr;
    protected int LastFlags;

    public ulong Key { get; protected set; }
    public ulong MirroredKey { get; protected set; }
    public bool Symmetric { get; }

    protected SyzygyTable(string path)
    {
        Path = path;
        string tableName = System.IO.Path.GetFileNameWithoutExtension(path);

        string normalized = SyzygyKeys.NormalizeName(tableName, mirror: false);
        string mirrored = SyzygyKeys.NormalizeName(tableName, mirror: true);
        Key = SyzygyKeys.FromName(normalized);
        MirroredKey = SyzygyKeys.FromName(mirrored);
        Symmetric = normalized == mirrored;

        Num = tableName.Length - 1;
        HasPawns = tableName.Contains('P');

        int v = tableName.IndexOf('v');
        string blackPart = tableName[..v];
        string whitePart = tableName[(v + 1)..];

        if (HasPawns)
        {
            Pawns[0] = whitePart.Count(c => c == 'P');
            Pawns[1] = blackPart.Count(c => c == 'P');
            if (Pawns[1] > 0 && (Pawns[0] == 0 || Pawns[1] < Pawns[0]))
            {
                (Pawns[0], Pawns[1]) = (Pawns[1], Pawns[0]);
            }
        }
        else
        {
            int j = 0;
            foreach (char pt in "KQRBNP")
            {
                if (blackPart.Count(c => c == pt) == 1) j++;
                if (whitePart.Count(c => c == pt) == 1) j++;
            }
            EncType = j >= 3 ? 0 : 2;
        }
    }

    protected void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            Map = new SyzygyMappedFile(Path);
            if (Map.Length % 64 != 16)
            {
                throw new InvalidDataException($"Invalid Syzygy file size: {Path}");
            }
            InitializeCore();
            _initialized = true;
        }
    }

    protected abstract void InitializeCore();

    // ---------- Header parsing helpers ----------

    protected void SetNormPiece(int[] norm, int[] pieces)
    {
        norm[0] = EncType == 0 ? 3 : 2;

        int i = norm[0];
        while (i < Num)
        {
            int j = i;
            while (j < Num && pieces[j] == pieces[i])
            {
                norm[i]++;
                j++;
            }
            i += norm[i];
        }
    }

    protected long CalcFactorsPiece(long[] factor, int order, int[] norm)
    {
        ReadOnlySpan<int> pivFac = [31332, 28056, 462];

        int n = 64 - norm[0];
        long f = 1;
        int i = norm[0];
        int k = 0;
        while (i < Num || k == order)
        {
            if (k == order)
            {
                factor[0] = f;
                f *= pivFac[EncType];
            }
            else
            {
                factor[i] = f;
                f *= SyzygyIndexing.SubFactor(norm[i], n);
                n -= norm[i];
                i += norm[i];
            }
            k++;
        }
        return f;
    }

    protected long CalcFactorsPawn(long[] factor, int order, int order2, int[] norm, int file)
    {
        int i = norm[0];
        if (order2 < 0x0f) i += norm[i];
        int n = 64 - i;

        long fac = 1;
        int k = 0;
        while (i < Num || k == order || k == order2)
        {
            if (k == order)
            {
                factor[0] = fac;
                fac *= SyzygyIndexing.PawnFactor[norm[0] - 1, file];
            }
            else if (k == order2)
            {
                factor[norm[0]] = fac;
                fac *= SyzygyIndexing.SubFactor(norm[norm[0]], 48 - norm[0]);
            }
            else
            {
                factor[i] = fac;
                fac *= SyzygyIndexing.SubFactor(norm[i], n);
                n -= norm[i];
                i += norm[i];
            }
            k++;
        }
        return fac;
    }

    protected void SetNormPawn(int[] norm, int[] pieces)
    {
        norm[0] = Pawns[0];
        if (Pawns[1] > 0) norm[Pawns[0]] = Pawns[1];

        int i = Pawns[0] + Pawns[1];
        while (i < Num)
        {
            int j = i;
            while (j < Num && pieces[j] == pieces[i])
            {
                norm[i]++;
                j++;
            }
            i += norm[i];
        }
    }

    private void CalcSymLen(SyzygyPairs d, int s, bool[] tmp)
    {
        long w = d.SymPat + 3 * s;
        int s2 = (Map.U8(w + 2) << 4) | (Map.U8(w + 1) >> 4);
        if (s2 == 0x0fff)
        {
            d.SymLen[s] = 0;
        }
        else
        {
            int s1 = ((Map.U8(w + 1) & 0xf) << 8) | Map.U8(w);
            if (!tmp[s1]) CalcSymLen(d, s1, tmp);
            if (!tmp[s2]) CalcSymLen(d, s2, tmp);
            d.SymLen[s] = d.SymLen[s1] + d.SymLen[s2] + 1;
        }
        tmp[s] = true;
    }

    protected SyzygyPairs SetupPairs(long dataPtr, long tbSize, int sizeIdx, bool wdl)
    {
        var d = new SyzygyPairs();

        LastFlags = Map.U8(dataPtr);
        if ((LastFlags & 0x80) != 0)
        {
            d.IdxBits = 0;
            d.MinLen = wdl ? Map.U8(dataPtr + 1) : 0;
            NextPtr = dataPtr + 2;
            Size[sizeIdx] = 0;
            Size[sizeIdx + 1] = 0;
            Size[sizeIdx + 2] = 0;
            return d;
        }

        d.BlockSize = Map.U8(dataPtr + 1);
        d.IdxBits = Map.U8(dataPtr + 2);

        long realNumBlocks = Map.U32(dataPtr + 4);
        long numBlocks = realNumBlocks + Map.U8(dataPtr + 3);
        int maxLen = Map.U8(dataPtr + 8);
        int minLen = Map.U8(dataPtr + 9);
        int h = maxLen - minLen + 1;
        int numSyms = Map.U16(dataPtr + 10 + 2 * h);

        d.Offset = dataPtr + 10;
        d.SymLen = new int[h * 8 + numSyms];
        d.SymPat = dataPtr + 12 + 2 * h;
        d.MinLen = minLen;

        NextPtr = dataPtr + 12 + 2 * h + 3 * numSyms + (numSyms & 1);

        long numIndices = (tbSize + (1L << d.IdxBits) - 1) >> d.IdxBits;
        Size[sizeIdx] = 6 * numIndices;
        Size[sizeIdx + 1] = 2 * numBlocks;
        Size[sizeIdx + 2] = (1L << d.BlockSize) * realNumBlocks;

        var tmp = new bool[numSyms];
        for (int i = 0; i < numSyms; i++)
        {
            if (!tmp[i]) CalcSymLen(d, i, tmp);
        }

        var baseValues = new long[h];
        baseValues[h - 1] = 0;
        for (int i = h - 2; i >= 0; i--)
        {
            baseValues[i] = (baseValues[i + 1] + Map.U16(d.Offset + i * 2L) - Map.U16(d.Offset + i * 2L + 2)) / 2;
        }

        d.Base = new ulong[h];
        for (int i = 0; i < h; i++)
        {
            d.Base[i] = (ulong)baseValues[i] << (64 - (minLen + i));
        }

        d.Offset -= 2L * d.MinLen;
        return d;
    }

    // ---------- Position encoding ----------

    protected static int PawnFile(int pawnCount, int[] pos)
    {
        for (int i = 1; i < pawnCount; i++)
        {
            if (SyzygyConstants.Flap[pos[0]] > SyzygyConstants.Flap[pos[i]])
            {
                (pos[0], pos[i]) = (pos[i], pos[0]);
            }
        }
        return SyzygyConstants.FileToFile[pos[0] & 0x07];
    }

    protected long EncodePiece(int[] norm, int[] pos, long[] factor)
    {
        int n = Num;

        if ((pos[0] & 0x04) != 0)
        {
            for (int i = 0; i < n; i++) pos[i] ^= 0x07;
        }

        if ((pos[0] & 0x20) != 0)
        {
            for (int i = 0; i < n; i++) pos[i] ^= 0x38;
        }

        int first;
        for (first = 0; first < n; first++)
        {
            if (SyzygyIndexing.OffDiag(pos[first]) != 0) break;
        }

        if (first < n && first < (EncType == 0 ? 3 : 2) && SyzygyIndexing.OffDiag(pos[first]) > 0)
        {
            for (int i = 0; i < n; i++) pos[i] = SyzygyIndexing.FlipDiag(pos[i]);
        }

        long idx;
        int k;
        if (EncType == 0)
        {
            int i = pos[1] > pos[0] ? 1 : 0;
            int j = (pos[2] > pos[0] ? 1 : 0) + (pos[2] > pos[1] ? 1 : 0);

            if (SyzygyIndexing.OffDiag(pos[0]) != 0)
            {
                idx = SyzygyConstants.Triangle[pos[0]] * 63L * 62 + (pos[1] - i) * 62L + (pos[2] - j);
            }
            else if (SyzygyIndexing.OffDiag(pos[1]) != 0)
            {
                idx = 6L * 63 * 62 + SyzygyConstants.Diag[pos[0]] * 28L * 62 + SyzygyConstants.Lower[pos[1]] * 62L + pos[2] - j;
            }
            else if (SyzygyIndexing.OffDiag(pos[2]) != 0)
            {
                idx = 6L * 63 * 62 + 4L * 28 * 62 + SyzygyConstants.Diag[pos[0]] * 7L * 28 + (SyzygyConstants.Diag[pos[1]] - i) * 28L + SyzygyConstants.Lower[pos[2]];
            }
            else
            {
                idx = 6L * 63 * 62 + 4L * 28 * 62 + 4L * 7 * 28 + SyzygyConstants.Diag[pos[0]] * 7L * 6 + (SyzygyConstants.Diag[pos[1]] - i) * 6L + (SyzygyConstants.Diag[pos[2]] - j);
            }
            k = 3;
        }
        else
        {
            idx = SyzygyConstants.KkIdx[SyzygyConstants.Triangle[pos[0]]][pos[1]];
            k = 2;
        }

        idx *= factor[0];

        while (k < n)
        {
            int t = norm[k];
            SortRange(pos, k, k + t);

            long s = 0;
            for (int m = k; m < k + t; m++)
            {
                int p = pos[m];
                int j = 0;
                for (int l = 0; l < k; l++) j += p > pos[l] ? 1 : 0;
                s += SyzygyIndexing.Binom(p - j, m - k + 1);
            }

            idx += s * factor[k];
            k += t;
        }

        return idx;
    }

    protected long EncodePawn(int[] norm, int[] pos, long[] factor)
    {
        int n = Num;

        if ((pos[0] & 0x04) != 0)
        {
            for (int i = 0; i < n; i++) pos[i] ^= 0x07;
        }

        for (int i = 1; i < Pawns[0]; i++)
        {
            for (int j = i + 1; j < Pawns[0]; j++)
            {
                if (SyzygyConstants.PTwist[pos[i]] < SyzygyConstants.PTwist[pos[j]])
                {
                    (pos[i], pos[j]) = (pos[j], pos[i]);
                }
            }
        }

        int t = Pawns[0] - 1;
        long idx = SyzygyIndexing.PawnIdx[t, SyzygyConstants.Flap[pos[0]]];
        for (int i = t; i > 0; i--)
        {
            idx += SyzygyIndexing.Binom(SyzygyConstants.PTwist[pos[i]], t - i + 1);
        }
        idx *= factor[0];

        int k = Pawns[0];
        t = k + Pawns[1];
        if (t > k)
        {
            SortRange(pos, k, t);

            long s = 0;
            for (int m = k; m < t; m++)
            {
                int p = pos[m];
                int j = 0;
                for (int l = 0; l < k; l++) j += p > pos[l] ? 1 : 0;
                s += SyzygyIndexing.Binom(p - j - 8, m - k + 1);
            }
            idx += s * factor[k];
            k = t;
        }

        while (k < n)
        {
            t = norm[k];
            SortRange(pos, k, k + t);

            long s = 0;
            for (int m = k; m < k + t; m++)
            {
                int p = pos[m];
                int j = 0;
                for (int l = 0; l < k; l++) j += p > pos[l] ? 1 : 0;
                s += SyzygyIndexing.Binom(p - j, m - k + 1);
            }
            idx += s * factor[k];
            k += t;
        }

        return idx;
    }

    private static void SortRange(int[] pos, int start, int end)
    {
        for (int j = start; j < end; j++)
        {
            for (int k = j + 1; k < end; k++)
            {
                if (pos[j] > pos[k]) (pos[j], pos[k]) = (pos[k], pos[j]);
            }
        }
    }

    // ---------- Block decompression ----------

    protected int DecompressPairs(SyzygyPairs d, long idx, bool dtz)
    {
        if (d.IdxBits == 0) return d.MinLen;

        long mainIdx = idx >> d.IdxBits;
        long litIdx = (idx & ((1L << d.IdxBits) - 1)) - (1L << (d.IdxBits - 1));
        long block = Map.U32(d.IndexTable + 6 * mainIdx);

        litIdx += Map.U16(d.IndexTable + 6 * mainIdx + 4);

        if (litIdx < 0)
        {
            while (litIdx < 0)
            {
                block--;
                litIdx += Map.U16(d.SizeTable + 2 * block) + 1;
            }
        }
        else
        {
            while (litIdx > Map.U16(d.SizeTable + 2 * block))
            {
                litIdx -= Map.U16(d.SizeTable + 2 * block) + 1;
                block++;
            }
        }

        long ptr = d.Data + (block << d.BlockSize);

        int m = d.MinLen;
        ulong code = Map.U64Be(ptr);
        ptr += 8;
        int bitCount = 0;

        int sym;
        while (true)
        {
            int l = m;
            while (code < d.Base[l - m]) l++;

            sym = Map.U16(d.Offset + l * 2L);
            sym += (int)((code - d.Base[l - m]) >> (64 - l));

            if (litIdx < d.SymLen[sym] + 1) break;

            litIdx -= d.SymLen[sym] + 1;
            code <<= l;
            bitCount += l;
            if (bitCount >= 32)
            {
                bitCount -= 32;
                code |= (ulong)Map.U32Be(ptr) << bitCount;
                ptr += 4;
            }
        }

        long symPat = d.SymPat;
        while (d.SymLen[sym] != 0)
        {
            long w = symPat + 3L * sym;
            int s1 = ((Map.U8(w + 1) & 0xf) << 8) | Map.U8(w);
            if (litIdx < d.SymLen[s1] + 1)
            {
                sym = s1;
            }
            else
            {
                litIdx -= d.SymLen[s1] + 1;
                sym = (Map.U8(w + 2) << 4) | (Map.U8(w + 1) >> 4);
            }
        }

        long wFinal = symPat + 3L * sym;
        return dtz ? ((Map.U8(wFinal + 1) & 0x0f) << 8) | Map.U8(wFinal) : Map.U8(wFinal);
    }

    // ---------- Board helpers ----------

    /// <summary>
    /// Appends the squares of the piece described by <paramref name="nibble"/> (type | color &lt;&lt; 3, flipped by
    /// <paramref name="colorMirror"/>) to <paramref name="p"/>, optionally mirrored vertically.
    /// </summary>
    protected static int Place(in Board board, int nibble, int colorMirror, int squareMirror, int[] p, int index)
    {
        int pieceType = nibble & 0x07;
        int color = (nibble ^ colorMirror) >> 3;
        ulong bb = board.GetPieces((PieceType)pieceType) & (color == 0 ? board.White : board.Black);

        while (bb != 0)
        {
            int sq = BitOperations.TrailingZeroCount(bb);
            bb &= bb - 1;
            p[index++] = sq ^ squareMirror;
        }
        return index;
    }

    /// <summary>
    /// Places every remaining piece group starting at <paramref name="start"/>. Throws if the table description
    /// does not match the board (guards against corrupt files looping forever).
    /// </summary>
    protected int PlaceAll(in Board board, int[] pieces, int start, int colorMirror, int squareMirror, int[] p)
    {
        int i = start;
        while (i < Num)
        {
            int next = Place(in board, pieces[i], colorMirror, squareMirror, p, i);
            if (next == i)
            {
                throw new InvalidDataException($"Syzygy table {System.IO.Path.GetFileName(Path)} does not match the probed position.");
            }
            i = next;
        }
        return i;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            Map.Dispose();
        }
    }
}

/// <summary>
/// Win/Draw/Loss table (.rtbw).
/// </summary>
internal sealed class SyzygyWdlTable : SyzygyTable
{
    private static ReadOnlySpan<byte> Magic => [0x71, 0xe8, 0x23, 0x5d];

    private long[] _tbSize = [];
    private readonly SyzygyPairs?[] _precomp = new SyzygyPairs?[2];
    private readonly int[][] _pieces = new int[2][];
    private readonly long[][] _factor = new long[2][];
    private readonly int[][] _norm = new int[2][];
    private readonly SyzygyPawnFile[] _files = new SyzygyPawnFile[4];

    public SyzygyWdlTable(string path) : base(path) { }

    protected override void InitializeCore()
    {
        if (!Map.MagicEquals(Magic))
        {
            throw new InvalidDataException($"Invalid Syzygy WDL magic: {Path}");
        }

        _tbSize = new long[8];
        Size = new long[24];

        bool split = (Map.U8(4) & 0x01) != 0;
        int files = (Map.U8(4) & 0x02) != 0 ? 4 : 1;
        long dataPtr = 5;

        if (!HasPawns)
        {
            SetupPiecesPiece(dataPtr);
            dataPtr += Num + 1;
            dataPtr += dataPtr & 1;

            _precomp[0] = SetupPairs(dataPtr, _tbSize[0], 0, true);
            dataPtr = NextPtr;
            if (split)
            {
                _precomp[1] = SetupPairs(dataPtr, _tbSize[1], 3, true);
                dataPtr = NextPtr;
            }

            _precomp[0]!.IndexTable = dataPtr;
            dataPtr += Size[0];
            if (split)
            {
                _precomp[1]!.IndexTable = dataPtr;
                dataPtr += Size[3];
            }

            _precomp[0]!.SizeTable = dataPtr;
            dataPtr += Size[1];
            if (split)
            {
                _precomp[1]!.SizeTable = dataPtr;
                dataPtr += Size[4];
            }

            dataPtr = (dataPtr + 0x3f) & ~0x3fL;
            _precomp[0]!.Data = dataPtr;
            dataPtr += Size[2];
            if (split)
            {
                dataPtr = (dataPtr + 0x3f) & ~0x3fL;
                _precomp[1]!.Data = dataPtr;
            }

            Key = SyzygyKeys.Recalc(_pieces[0], mirror: false);
            MirroredKey = SyzygyKeys.Recalc(_pieces[0], mirror: true);
        }
        else
        {
            int s = 1 + (Pawns[1] > 0 ? 1 : 0);
            for (int f = 0; f < 4; f++)
            {
                _files[f] = new SyzygyPawnFile();
                SetupPiecesPawn(dataPtr, 2 * f, f);
                dataPtr += Num + s;
            }
            dataPtr += dataPtr & 1;

            for (int f = 0; f < files; f++)
            {
                _files[f].Precomp[0] = SetupPairs(dataPtr, _tbSize[2 * f], 6 * f, true);
                dataPtr = NextPtr;
                if (split)
                {
                    _files[f].Precomp[1] = SetupPairs(dataPtr, _tbSize[2 * f + 1], 6 * f + 3, true);
                    dataPtr = NextPtr;
                }
            }

            for (int f = 0; f < files; f++)
            {
                _files[f].Precomp[0]!.IndexTable = dataPtr;
                dataPtr += Size[6 * f];
                if (split)
                {
                    _files[f].Precomp[1]!.IndexTable = dataPtr;
                    dataPtr += Size[6 * f + 3];
                }
            }

            for (int f = 0; f < files; f++)
            {
                _files[f].Precomp[0]!.SizeTable = dataPtr;
                dataPtr += Size[6 * f + 1];
                if (split)
                {
                    _files[f].Precomp[1]!.SizeTable = dataPtr;
                    dataPtr += Size[6 * f + 4];
                }
            }

            for (int f = 0; f < files; f++)
            {
                dataPtr = (dataPtr + 0x3f) & ~0x3fL;
                _files[f].Precomp[0]!.Data = dataPtr;
                dataPtr += Size[6 * f + 2];
                if (split)
                {
                    dataPtr = (dataPtr + 0x3f) & ~0x3fL;
                    _files[f].Precomp[1]!.Data = dataPtr;
                    dataPtr += Size[6 * f + 5];
                }
            }
        }
    }

    private void SetupPiecesPiece(long pData)
    {
        for (int side = 0; side < 2; side++)
        {
            _pieces[side] = new int[Num];
            for (int i = 0; i < Num; i++)
            {
                int b = Map.U8(pData + i + 1);
                _pieces[side][i] = side == 0 ? b & 0x0f : b >> 4;
            }

            int order = side == 0 ? Map.U8(pData) & 0x0f : Map.U8(pData) >> 4;
            _norm[side] = new int[Num];
            _factor[side] = new long[TbPieces];
            SetNormPiece(_norm[side], _pieces[side]);
            _tbSize[side] = CalcFactorsPiece(_factor[side], order, _norm[side]);
        }
    }

    private void SetupPiecesPawn(long pData, int pTbSize, int f)
    {
        int j = 1 + (Pawns[1] > 0 ? 1 : 0);

        for (int side = 0; side < 2; side++)
        {
            int order = side == 0 ? Map.U8(pData) & 0x0f : Map.U8(pData) >> 4;
            int order2 = Pawns[1] > 0
                ? (side == 0 ? Map.U8(pData + 1) & 0x0f : Map.U8(pData + 1) >> 4)
                : 0x0f;

            var pieces = new int[Num];
            for (int i = 0; i < Num; i++)
            {
                int b = Map.U8(pData + i + j);
                pieces[i] = side == 0 ? b & 0x0f : b >> 4;
            }

            _files[f].Pieces[side] = pieces;
            _files[f].Norm[side] = new int[Num];
            SetNormPawn(_files[f].Norm[side], pieces);
            _files[f].Factor[side] = new long[TbPieces];
            _tbSize[pTbSize + side] = CalcFactorsPawn(_files[f].Factor[side], order, order2, _files[f].Norm[side], f);
        }
    }

    /// <summary>
    /// Probes the table; <paramref name="board"/> must have a matching material signature.
    /// Returns WDL in the range -2..2 for the side to move (without capture resolution).
    /// </summary>
    public int Probe(in Board board)
    {
        EnsureInitialized();

        ulong key = SyzygyKeys.FromBoard(in board);
        bool white = board.SideToMove == Color.White;

        int cMirror, mirror, bSide;
        if (!Symmetric)
        {
            if (key != Key)
            {
                cMirror = 8;
                mirror = 0x38;
                bSide = white ? 1 : 0;
            }
            else
            {
                cMirror = mirror = 0;
                bSide = white ? 0 : 1;
            }
        }
        else
        {
            cMirror = white ? 0 : 8;
            mirror = white ? 0 : 0x38;
            bSide = 0;
        }

        var p = new int[TbPieces];
        int res;

        if (!HasPawns)
        {
            PlaceAll(in board, _pieces[bSide], 0, cMirror, 0, p);

            long idx = EncodePiece(_norm[bSide], p, _factor[bSide]);
            res = DecompressPairs(_precomp[bSide] ?? throw new InvalidDataException("Missing WDL sub-table"), idx, dtz: false);
        }
        else
        {
            // The leading pawn group is described by file 0's layout; remaining groups by the selected file.
            int i = Place(in board, _files[0].Pieces[0][0], cMirror, mirror, p, 0);
            int f = PawnFile(Pawns[0], p);
            PlaceAll(in board, _files[f].Pieces[bSide], i, cMirror, mirror, p);

            long idx = EncodePawn(_files[f].Norm[bSide], p, _files[f].Factor[bSide]);
            res = DecompressPairs(_files[f].Precomp[bSide] ?? throw new InvalidDataException("Missing WDL sub-table"), idx, dtz: false);
        }

        return res - 2;
    }
}

/// <summary>
/// Distance-to-zeroing table (.rtbz).
/// </summary>
internal sealed class SyzygyDtzTable : SyzygyTable
{
    private static ReadOnlySpan<byte> Magic => [0xd7, 0x66, 0x0c, 0xa5];

    private long[] _tbSize = [];
    private SyzygyPairs _precomp = null!;
    private int[] _pieces = [];
    private long[] _factor = [];
    private int[] _norm = [];
    private readonly SyzygyPawnFileDtz[] _files = new SyzygyPawnFileDtz[4];

    private int _flags;
    private int[] _fileFlags = [];
    private int[][] _mapIdx = [];
    private long _pMap;

    public SyzygyDtzTable(string path) : base(path) { }

    protected override void InitializeCore()
    {
        if (!Map.MagicEquals(Magic))
        {
            throw new InvalidDataException($"Invalid Syzygy DTZ magic: {Path}");
        }

        _factor = new long[TbPieces];
        _norm = new int[Num];
        _tbSize = new long[4];
        Size = new long[12];

        int files = (Map.U8(4) & 0x02) != 0 ? 4 : 1;
        long pData = 5;

        if (!HasPawns)
        {
            _mapIdx = [new int[4]];

            SetupPiecesPieceDtz(pData);
            pData += Num + 1;
            pData += pData & 1;

            _precomp = SetupPairs(pData, _tbSize[0], 0, false);
            _flags = LastFlags;
            pData = NextPtr;
            _pMap = pData;

            if ((_flags & 2) != 0)
            {
                if ((_flags & 16) == 0)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _mapIdx[0][i] = (int)(pData + 1 - _pMap);
                        pData += 1 + Map.U8(pData);
                    }
                }
                else
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _mapIdx[0][i] = (int)((pData + 2 - _pMap) / 2);
                        pData += 2 + 2 * Map.U16(pData);
                    }
                }
            }
            pData += pData & 1;

            _precomp.IndexTable = pData;
            pData += Size[0];

            _precomp.SizeTable = pData;
            pData += Size[1];

            pData = (pData + 0x3f) & ~0x3fL;
            _precomp.Data = pData;

            Key = SyzygyKeys.Recalc(_pieces, mirror: false);
            MirroredKey = SyzygyKeys.Recalc(_pieces, mirror: true);
        }
        else
        {
            int s = 1 + (Pawns[1] > 0 ? 1 : 0);
            for (int f = 0; f < 4; f++)
            {
                _files[f] = new SyzygyPawnFileDtz();
                SetupPiecesPawnDtz(pData, f, f);
                pData += Num + s;
            }
            pData += pData & 1;

            _fileFlags = new int[files];
            for (int f = 0; f < files; f++)
            {
                _files[f].Precomp = SetupPairs(pData, _tbSize[f], 3 * f, false);
                pData = NextPtr;
                _fileFlags[f] = LastFlags;
            }

            _mapIdx = new int[files][];
            _pMap = pData;
            for (int f = 0; f < files; f++)
            {
                _mapIdx[f] = new int[4];
                if ((_fileFlags[f] & 2) != 0)
                {
                    if ((_fileFlags[f] & 16) == 0)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            _mapIdx[f][i] = (int)(pData + 1 - _pMap);
                            pData += 1 + Map.U8(pData);
                        }
                    }
                    else
                    {
                        pData += pData & 1;
                        for (int i = 0; i < 4; i++)
                        {
                            _mapIdx[f][i] = (int)((pData + 2 - _pMap) / 2);
                            pData += 2 + 2 * Map.U16(pData);
                        }
                    }
                }
            }
            pData += pData & 1;

            for (int f = 0; f < files; f++)
            {
                _files[f].Precomp.IndexTable = pData;
                pData += Size[3 * f];
            }

            for (int f = 0; f < files; f++)
            {
                _files[f].Precomp.SizeTable = pData;
                pData += Size[3 * f + 1];
            }

            for (int f = 0; f < files; f++)
            {
                pData = (pData + 0x3f) & ~0x3fL;
                _files[f].Precomp.Data = pData;
                pData += Size[3 * f + 2];
            }
        }
    }

    private void SetupPiecesPieceDtz(long pData)
    {
        _pieces = new int[Num];
        for (int i = 0; i < Num; i++) _pieces[i] = Map.U8(pData + i + 1) & 0x0f;

        int order = Map.U8(pData) & 0x0f;
        SetNormPiece(_norm, _pieces);
        _tbSize[0] = CalcFactorsPiece(_factor, order, _norm);
    }

    private void SetupPiecesPawnDtz(long pData, int pTbSize, int f)
    {
        int j = 1 + (Pawns[1] > 0 ? 1 : 0);
        int order = Map.U8(pData) & 0x0f;
        int order2 = Pawns[1] > 0 ? Map.U8(pData + 1) & 0x0f : 0x0f;

        var pieces = new int[Num];
        for (int i = 0; i < Num; i++) pieces[i] = Map.U8(pData + i + j) & 0x0f;
        _files[f].Pieces = pieces;

        _files[f].Norm = new int[Num];
        SetNormPawn(_files[f].Norm, pieces);

        _files[f].Factor = new long[TbPieces];
        _tbSize[pTbSize] = CalcFactorsPawn(_files[f].Factor, order, order2, _files[f].Norm, f);
    }

    /// <summary>
    /// Returns false if this table does not store the requested side to move.
    /// <paramref name="dtz"/> is the stored (rounded) distance for the given WDL value.
    /// </summary>
    public bool Probe(in Board board, int wdl, out int dtz)
    {
        EnsureInitialized();
        dtz = 0;

        ulong key = SyzygyKeys.FromBoard(in board);
        bool white = board.SideToMove == Color.White;

        int cMirror, mirror, bSide;
        if (!Symmetric)
        {
            if (key != Key)
            {
                cMirror = 8;
                mirror = 0x38;
                bSide = white ? 1 : 0;
            }
            else
            {
                cMirror = mirror = 0;
                bSide = white ? 0 : 1;
            }
        }
        else
        {
            cMirror = white ? 0 : 8;
            mirror = white ? 0 : 0x38;
            bSide = 0;
        }

        var p = new int[TbPieces];
        int res;

        if (!HasPawns)
        {
            if ((_flags & 1) != bSide && !Symmetric) return false;

            PlaceAll(in board, _pieces, 0, cMirror, 0, p);

            long idx = EncodePiece(_norm, p, _factor);
            res = DecompressPairs(_precomp, idx, dtz: true);

            if ((_flags & 2) != 0)
            {
                int map = _mapIdx[0][SyzygyConstants.WdlToMap[wdl + 2]];
                res = (_flags & 16) == 0
                    ? Map.U8(_pMap + map + res)
                    : Map.U16(_pMap + 2L * (map + res));
            }

            if ((_flags & SyzygyConstants.PaFlags[wdl + 2]) == 0 || (wdl & 1) != 0)
            {
                res *= 2;
            }
        }
        else
        {
            int i = Place(in board, _files[0].Pieces[0], cMirror, mirror, p, 0);
            int f = PawnFile(Pawns[0], p);
            if ((_fileFlags[f] & 1) != bSide) return false;

            PlaceAll(in board, _files[f].Pieces, i, cMirror, mirror, p);

            long idx = EncodePawn(_files[f].Norm, p, _files[f].Factor);
            res = DecompressPairs(_files[f].Precomp, idx, dtz: true);

            if ((_fileFlags[f] & 2) != 0)
            {
                int map = _mapIdx[f][SyzygyConstants.WdlToMap[wdl + 2]];
                res = (_fileFlags[f] & 16) == 0
                    ? Map.U8(_pMap + map + res)
                    : Map.U16(_pMap + 2L * (map + res));
            }

            if ((_fileFlags[f] & SyzygyConstants.PaFlags[wdl + 2]) == 0 || (wdl & 1) != 0)
            {
                res *= 2;
            }
        }

        dtz = res;
        return true;
    }
}
