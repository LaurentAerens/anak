using System.Runtime.InteropServices;
using System.Text;

namespace Anak.Data.Datagen;

/// <summary>
/// 64-byte binary header for Anak .binpack dataset files.
/// Compatible with Anak's Python and C++ memory-mapped binpack loaders.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public unsafe struct AnakBinpackHeader
{
    public fixed byte Magic[8];          // "ANAKBINP"
    public uint Version;                // 1
    public uint RecordSize;             // 144
    public ulong RecordCount;           // total written records
    public fixed byte ModelTag[16];     // "open", "closed", "endgame", etc.
    public fixed byte Reserved[24];     // zero-filled

    public static AnakBinpackHeader Create(string modelTag, ulong recordCount = 0)
    {
        var header = new AnakBinpackHeader
        {
            Version = 1,
            RecordSize = 144,
            RecordCount = recordCount
        };

        byte[] magicBytes = Encoding.ASCII.GetBytes("ANAKBINP");
        for (int i = 0; i < 8; i++) header.Magic[i] = magicBytes[i];

        byte[] tagBytes = Encoding.ASCII.GetBytes(modelTag);
        int len = Math.Min(tagBytes.Length, 15);
        for (int i = 0; i < len; i++) header.ModelTag[i] = tagBytes[i];

        return header;
    }

    public string GetMagicString()
    {
        fixed (byte* pMagic = Magic)
        {
            return Encoding.ASCII.GetString(pMagic, 8);
        }
    }

    public string GetModelTagString()
    {
        fixed (byte* pTag = ModelTag)
        {
            int len = 0;
            while (len < 16 && pTag[len] != 0) len++;
            return Encoding.ASCII.GetString(pTag, len);
        }
    }
}
